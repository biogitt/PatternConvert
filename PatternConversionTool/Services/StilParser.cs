using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using PatternConversionTool.Models;

namespace PatternConversionTool.Services;

/// <summary>
/// Simplified STIL parser that supports the ATPG subset produced by TetraMAX / DFT Compiler.
/// </summary>
public class StilParser : IStilParser
{
    // ?? parsed intermediate data ????????????????????????????????????
    private readonly List<StilSig> _signals = new();
    private readonly Dictionary<string, List<string>> _groups = new();   // resolved
    private readonly List<TimeSetDef> _timeSets = new();
    private readonly List<string> _timeSetOrder = new();
    private readonly Dictionary<string, ProcDef> _procedures = new();
    private readonly Dictionary<string, ProcDef> _macroDefs = new();
    private Dictionary<string, string> _ioWfcMap = new();

    // Scan group names flagged ScanIn / ScanOut (e.g. "_chain1_A2D_SCAN_SDI_I_").
    // The scan-data blocks are keyed by these group names, so the expander looks
    // them up in addition to the resolved member signal names.
    private readonly List<string> _siGroupNames = new();
    private readonly List<string> _soGroupNames = new();

    private string[] _lines = [];
    private int _pos;

    // Global label bookkeeping used to reproduce the reference tool's label
    // de-duplication. Every label encountered during pattern expansion (the
    // precondition, each "pattern N" call label, each load_unload's internal
    // "Internal_scan_pre_shift" label and the capture labels) advances
    // _labelOrdinal. When a label name repeats, a "_N" suffix is appended where
    // N is the ordinal of the capture's first label.
    private int _labelOrdinal;
    private readonly HashSet<string> _seenLabels = new(StringComparer.Ordinal);

    /// <summary>
    /// Optional cap on the number of expanded vector rows. A value of 0 (the
    /// default) means unlimited. Useful for previewing / diffing only the first
    /// portion of a very large pattern without materialising every vector in
    /// memory.
    /// </summary>
    public int MaxVectors { get; set; }

    /// <summary>
    /// Optional sink for expanded vector rows. When set, <see cref="ParsePattern"/>
    /// streams each row to this callback instead of collecting them in
    /// <see cref="PatternInfo.Vectors"/>, keeping memory flat for huge patterns.
    /// </summary>
    private Action<VectorRow>? _rowSink;
    private int _emittedRows;

    /// <summary>Emit one expanded row: either stream it to the sink or collect it
    /// in the pattern's vector list when no sink is attached.</summary>
    private void Emit(PatternInfo pat, VectorRow row)
    {
        _emittedRows++;
        if (_rowSink != null) _rowSink(row);
        else pat.Vectors.Add(row);
    }

    /// <summary>Number of rows expanded so far (whether streamed or collected).
    /// Lets the streaming caller bound the work and report progress.</summary>
    private int EmittedRows => _emittedRows;

    // ?? public entry point ??????????????????????????????????????????
    public StilParseResult Parse(string filePath, bool expandPattern = true)
    {
        ResetState();
        _lines = File.ReadAllLines(filePath);

        while (_pos < _lines.Length)
        {
            string line = _lines[_pos].Trim();
            // Block dispatch. Names after the keyword may be quoted ("IDDQ_timing")
            // or bare (IDDQ_timing). The Timing block header opens a brace block;
            // the "Timing name;" reference inside a PatternExec must not be treated
            // as a block, so require a '{' on (or opened by) the Timing line.
            if (line.StartsWith("Signals") && !line.StartsWith("SignalGroups")) ParseSignals();
            else if (line.StartsWith("SignalGroups")) ParseSignalGroups();
            else if (Regex.IsMatch(line, @"^Timing\b") && line.Contains("{")) ParseTiming();
            else if (line.StartsWith("Procedures")) ParseProcedures();
            else if (line.StartsWith("MacroDefs"))  ParseMacroDefs();
            else if (Regex.IsMatch(line, @"^Pattern\s+")) return BuildResult(filePath, expandPattern);
            else _pos++;
        }
        return BuildResult(filePath, expandPattern);
    }

    /// <summary>Parse a STIL file, streaming each expanded vector row to
    /// <paramref name="rowSink"/> rather than collecting them. Keeps memory flat
    /// for patterns with millions of cycles.</summary>
    public StilParseResult ParseStreaming(string filePath, Action<VectorRow> rowSink)
    {
        _rowSink = rowSink;
        try
        {
            return Parse(filePath, expandPattern: true);
        }
        finally
        {
            _rowSink = null;
        }
    }

    private void ResetState()
    {
        _pos = 0;
        _labelOrdinal = 0;
        _emittedRows = 0;
        _seenLabels.Clear();

        // Reset intermediate state so the parser can be reused safely (e.g. a
        // fast metadata-only load in the UI followed by a full parse at generate).
        _signals.Clear();
        _groups.Clear();
        _timeSets.Clear();
        _timeSetOrder.Clear();
        _procedures.Clear();
        _macroDefs.Clear();
        _ioWfcMap.Clear();
        _siGroupNames.Clear();
        _soGroupNames.Clear();
    }

    // ?? Signals { � } ??????????????????????????????????????????????
    private void ParseSignals()
    {
        string block = ReadBlock();
        // Signal names may be quoted ("A2D_SCLK_SDA") or bare (A2D_SCLK_SDA).
        foreach (Match m in Regex.Matches(block,
            @"(?:""([^""]+)""|([A-Za-z_]\w*))\s+(In|Out|InOut)\s*;?\s*(\{\s*(ScanIn|ScanOut)\s*;\s*\})?"))
        {
            string name = m.Groups[1].Success ? m.Groups[1].Value : m.Groups[2].Value;
            _signals.Add(new StilSig(name, m.Groups[3].Value,
                m.Groups[5].Success ? m.Groups[5].Value : ""));
        }
    }

    // ?? SignalGroups { � } ?????????????????????????????????????????
    private void ParseSignalGroups()
    {
        string block = ReadBlock();
        var lines = block.Split('\n');
        int i = 0;
        while (i < lines.Length)
        {
            // Group name may be quoted ("_chain1_...") or bare (PI_grp_0).
            var gm = Regex.Match(lines[i].Trim(),
                @"^(?:""([^""]+)""|([A-Za-z_]\w*))\s*=\s*'");
            if (gm.Success)
            {
                string name = gm.Groups[1].Success ? gm.Groups[1].Value : gm.Groups[2].Value;
                var sb = new StringBuilder(lines[i]);
                while (!lines[i].Contains(';') && i + 1 < lines.Length)
                    sb.Append(' ').Append(lines[++i]);

                string full = sb.ToString();
                var sec = Regex.Match(full, @"'(.+?)'");
                if (sec.Success)
                {
                    // Members inside the quotes may be quoted or bare, joined by '+'.
                    var raw = Regex.Matches(sec.Groups[1].Value,
                            @"""([^""]+)""|([A-Za-z_]\w*)")
                        .Cast<Match>()
                        .Select(m => m.Groups[1].Success ? m.Groups[1].Value : m.Groups[2].Value)
                        .ToList();

                    // resolve nested groups
                    var resolved = new List<string>();
                    foreach (var s in raw)
                        resolved.AddRange(_groups.TryGetValue(s, out var g) ? g : [s]);
                    _groups[name] = resolved;

                    // A group flagged ScanIn / ScanOut is the scan-input / scan-output
                    // group. Register it under the canonical "_si"/"_so" names the
                    // expander looks up, so scan chains named "_chain1_..._" work too.
                    if (Regex.IsMatch(full, @"\bScanIn\b") && !_groups.ContainsKey("_si"))
                        _groups["_si"] = resolved;
                    if (Regex.IsMatch(full, @"\bScanOut\b") && !_groups.ContainsKey("_so"))
                        _groups["_so"] = resolved;

                    // Remember the scan group's own name too; the scan-data blocks
                    // are keyed by it (e.g. "_chain1_A2D_SCAN_SDI_I_" = 0011...).
                    if (Regex.IsMatch(full, @"\bScanIn\b")) _siGroupNames.Add(name);
                    if (Regex.IsMatch(full, @"\bScanOut\b")) _soGroupNames.Add(name);
                }

                // WFCMap for _io group
                if (name == "_io" && full.Contains("WFCMap"))
                {
                    foreach (Match wm in Regex.Matches(full, @"(\w+)->(\w+)"))
                        _ioWfcMap[wm.Groups[1].Value] = wm.Groups[2].Value;
                }
            }
            i++;
        }
    }

    // ?? Timing { � } ??????????????????????????????????????????????
    private void ParseTiming()
    {
        string block = ReadBlock();
        var lines = block.Split('\n');
        int i = 0;
        while (i < lines.Length)
        {
            // WaveformTable name may be quoted or bare (tset_gen_tp1).
            var wm = Regex.Match(lines[i].Trim(),
                @"WaveformTable\s+(?:""([^""]+)""|([A-Za-z_]\w*))");
            if (wm.Success)
            {
                string tsName = wm.Groups[1].Success ? wm.Groups[1].Value : wm.Groups[2].Value;
                var ts = new TimeSetDef { Name = tsName };
                _timeSetOrder.Add(ts.Name);
                // find Period
                while (i < lines.Length)
                {
                    var pm = Regex.Match(lines[++i], @"Period\s+'([^']+)'");
                    if (pm.Success) { ts.PeriodSeconds = ParseTime(pm.Groups[1].Value); break; }
                }
                // waveforms until closing brace pair
                int depth = 0;
                while (++i < lines.Length)
                {
                    string l = lines[i].Trim();
                    depth += l.Count(c => c == '{') - l.Count(c => c == '}');
                    // Signal/group name may be quoted or bare (PI_grp_0). The WFC
                    // token may be a single char ("P") or a compact set ("01N"),
                    // and each edge's action may carry slash-separated branches
                    // ("D/U/N"), one per WFC in the set.
                    var em = Regex.Match(l,
                        @"(?:""([^""]+)""|([A-Za-z_]\w*))\s*\{\s*(\w+)\s*\{(.+?)\}\s*\}");
                    if (em.Success)
                    {
                        string sigOrGroup = em.Groups[1].Success ? em.Groups[1].Value : em.Groups[2].Value;
                        string wfcSet = em.Groups[3].Value;
                        string edgesBody = em.Groups[4].Value;
                        foreach (var wf in ExpandWaveforms(sigOrGroup, wfcSet, edgesBody))
                            ts.Waveforms.Add(wf);
                    }
                    if (depth <= -1) break;
                }
                _timeSets.Add(ts);
            }
            i++;
        }
    }

    /// <summary>Expand one STIL waveform entry into one <see cref="WaveformDef"/>
    /// per WFC in <paramref name="wfcSet"/>. STIL allows a compact form where a
    /// single entry defines several WFCs at once (e.g. <c>01N { '0ns' D/U/N; }</c>)
    /// and each edge's action is a slash-separated list, one branch per WFC. The
    /// legacy single-WFC form (e.g. <c>P { '0ns' D; '50ns' U; '100ns' D; }</c>) is
    /// handled as the trivial one-branch case.</summary>
    private IEnumerable<WaveformDef> ExpandWaveforms(string sigOrGroup, string wfcSet, string edgesBody)
    {
        // Parse each edge as (time, [branch actions]).
        var edges = Regex.Matches(edgesBody, @"'([^']+)'\s+([A-Za-z](?:\s*/\s*[A-Za-z])*)")
            .Cast<Match>()
            .Select(m => new
            {
                Time = ParseTime(m.Groups[1].Value),
                Branches = m.Groups[2].Value.Split('/').Select(x => x.Trim()[0]).ToArray()
            })
            .ToList();

        for (int k = 0; k < wfcSet.Length; k++)
        {
            char wfc = wfcSet[k];
            var wf = new WaveformDef { SignalOrGroup = sigOrGroup, WFC = wfc };
            foreach (var e in edges)
            {
                // Pick this WFC's branch; fall back to the last branch when a
                // single action applies to all WFCs.
                char action = k < e.Branches.Length ? e.Branches[k] : e.Branches[^1];
                wf.Edges.Add(new EdgeDef { TimeSeconds = e.Time, Action = action });
            }
            yield return wf;
        }
    }

    // ?? Procedures / MacroDefs ?????????????????????????????????????
    private void ParseProcedures() => ParseProcBlock(_procedures);
    private void ParseMacroDefs()  => ParseProcBlock(_macroDefs);

    /// <summary>Extract the first <c>W</c> waveform-table reference from a body.
    /// The name may be quoted (<c>W "wft";</c>) or bare (<c>W wft;</c>).</summary>
    private static string WftName(string body)
    {
        var m = Regex.Match(body, @"\bW\s+(?:""([^""]+)""|([A-Za-z_]\w*))\s*;");
        return !m.Success ? "" : (m.Groups[1].Success ? m.Groups[1].Value : m.Groups[2].Value);
    }

    private void ParseProcBlock(Dictionary<string, ProcDef> target)
    {
        string block = ReadBlock();
        var lines = block.Split('\n');
        int i = 0;
        while (i < lines.Length)
        {
            var pm = Regex.Match(lines[i].Trim(), @"^""([^""]+)""\s*\{");
            if (pm.Success)
            {
                var pd = new ProcDef { Name = pm.Groups[1].Value };
                int depth = 1;
                var body = new StringBuilder();
                while (depth > 0 && ++i < lines.Length)
                {
                    depth += lines[i].Count(c => c == '{') - lines[i].Count(c => c == '}');
                    body.AppendLine(lines[i]);
                }
                pd.Body = body.ToString();
                pd.WFT = WftName(pd.Body);
                pd.HasShift = pd.Body.Contains("Shift");
                target[pd.Name] = pd;
            }
            i++;
        }
    }

    // ?? Pattern expansion ??????????????????????????????????????????
    // Instead of storing a raw AST we expand directly into VectorRows
    // which makes the generator trivial.

    private StilParseResult BuildResult(string filePath, bool expandPattern = true)
    {
        var pattern = new PatternInfo
        {
            TimeSetOrder = new List<string>(_timeSetOrder),
            EstimatedCycles = ReadEstimatedCycles()
        };

        // try to find Pattern block and expand it
        for (int i = 0; i < _lines.Length; i++)
        {
            if (Regex.IsMatch(_lines[i].Trim(), @"^Pattern\s+"))
            {
                // Pattern name may be quoted ("_pattern_") or bare (scan_test).
                var nm = Regex.Match(_lines[i], @"^\s*Pattern\s+(?:""([^""]+)""|([A-Za-z_]\w*))");
                if (nm.Success)
                    pattern.PatternName = nm.Groups[1].Success
                        ? nm.Groups[1].Value : nm.Groups[2].Value;

                // Skip the (potentially huge) vector expansion when only the
                // configuration is needed. The pattern name above is still
                // captured cheaply for naming the output files.
                if (expandPattern)
                {
                    _pos = i + 1;
                    ExpandPattern(pattern);
                }
                break;
            }
        }

        // build Signal list
        var signals = _signals.Select(s => new Signal
        {
            Name = s.Name,
            Direction = s.Dir,
            Enabled = true,
            Source = "STIL",
            OriginalStilName = s.Name,
            Group = s.Scan != "" ? "Scan" : ""
        }).ToList();

        return new StilParseResult
        {
            Signals = signals,
            SignalGroups = _groups.Select(kv =>
                new SignalGroup { Name = kv.Key, SignalNames = kv.Value }).ToList(),
            Timing = new TimingInfo { TimeSets = _timeSets },
            Pattern = pattern,
            AllInputSignals = Grp("all_inputs"),
            AllOutputSignals = Grp("all_outputs"),
            AllBidirSignals = Grp("all_bidirectionals")
        };
    }

    private List<string> Grp(string name) =>
        _groups.TryGetValue(name, out var g) ? g : [];

    /// <summary>Read the approximate cycle count from the STIL footer annotation
    /// "Patterns reference N V statements, generating M test cycles". Returns 0
    /// when not present. Scans from the end since the comment is near the bottom.</summary>
    private long ReadEstimatedCycles()
    {
        for (int i = _lines.Length - 1; i >= 0 && i > _lines.Length - 200; i--)
        {
            var m = Regex.Match(_lines[i], @"generating\s+(\d+)\s+test\s+cycles");
            if (m.Success && long.TryParse(m.Groups[1].Value, out var n))
                return n;
        }
        return 0;
    }

    // ?? expand Pattern block into flat VectorRows ??????????????????
    private void ExpandPattern(PatternInfo pat)
    {
        // Per-signal current state (STIL signal names)
        var cur = new Dictionary<string, char>();
        foreach (var s in _signals) cur[s.Name] = 'X';

        string curWFT = "_default_WFT_";
        string? pendingLabel = null;   // standalone "label:" waiting for its vector
        bool pendingIddq = false;      // IddqTestPoint; seen, applies to next vector

        while (_pos < _lines.Length)
        {
            if (MaxVectors > 0 && EmittedRows >= MaxVectors) break;

            string line = _lines[_pos].Trim();
            if (line == "}") { pat.IsComplete = true; break; }
            if (line == "{") { _pos++; continue; }
            if (line.Length == 0) { _pos++; continue; }

            // W "wft";  or  W wft;
            var wm = Regex.Match(line, @"^\s*W\s+(?:""([^""]+)""|([A-Za-z_]\w*))\s*;");
            if (wm.Success)
            {
                curWFT = wm.Groups[1].Success ? wm.Groups[1].Value : wm.Groups[2].Value;
                _pos++; continue;
            }

            // Ann {* � *}
            var am = Regex.Match(line, @"Ann\s*\{\*\s*(.+?)\s*\*\}");
            if (am.Success)
            {
                Emit(pat, new VectorRow { Comment = am.Groups[1].Value });
                _pos++; continue;
            }

            // IddqTestPoint;  (flag the next emitted vector)
            if (Regex.IsMatch(line, @"^IddqTestPoint\s*;"))
            {
                pendingIddq = true;
                _pos++; continue;
            }

            // Standalone label line: "pattern 0":  with nothing else on the line.
            // The label attaches to the next vector-producing statement.
            var soloLabel = Regex.Match(line, @"^(?:""([^""]+)""|([A-Za-z_]\w*))\s*:\s*$");
            if (soloLabel.Success)
            {
                pendingLabel = soloLabel.Groups[1].Success
                    ? soloLabel.Groups[1].Value : soloLabel.Groups[2].Value;
                _pos++; continue;
            }

            // Macro "name"  (optionally with an inline { body }). A macro whose
            // definition contains a Shift drives the scan load/unload using the
            // scan data in its body; otherwise the body's assignments produce a
            // single capture vector.
            var macroMatch = Regex.Match(line,
                @"^(?:(?:""([^""]+)""|([A-Za-z_]\w*))\s*:\s*)?Macro\s+""([^""]+)""");
            if (macroMatch.Success)
            {
                string macName = macroMatch.Groups[3].Value;
                string? macLabel = pendingLabel;
                if (macroMatch.Groups[1].Success) macLabel = macroMatch.Groups[1].Value;
                else if (macroMatch.Groups[2].Success) macLabel = macroMatch.Groups[2].Value;
                pendingLabel = null;

                bool hasBody = line.Contains('{');
                if (hasBody)
                {
                    string body = ReadCallBody();
                    if (_macroDefs.TryGetValue(macName, out var mdef) && mdef.HasShift)
                        ExpandLoadUnload(mdef, body, cur, ref curWFT, pat, macLabel);
                    else if (_macroDefs.TryGetValue(macName, out var cdef))
                        ExpandMacroCapture(cdef, body, cur, ref curWFT, pat, macLabel, ref pendingIddq);
                    else
                        ExpandMacro(macName, cur, ref curWFT, pat);
                    continue;   // ReadCallBody advanced _pos
                }

                ExpandMacro(macName, cur, ref curWFT, pat);
                _pos++; continue;
            }

            // Top-level V { assignments } (this dialect emits vectors directly in
            // the Pattern block). Apply the state and emit one vector.
            if (Regex.IsMatch(line, @"^V\s*\{"))
            {
                string vBody = ReadCallBody();
                var vm2 = Regex.Match(vBody, @"V\s*\{(.+)\}", RegexOptions.Singleline);
                if (vm2.Success) ApplyAssignments(vm2.Groups[1].Value, cur);
                Emit(pat, MakeRow(cur, curWFT, pendingLabel));
                if (pendingIddq)
                {
                    // Retag the just-emitted row (it is the last one collected).
                    pendingIddq = false;
                }
                pendingLabel = null;
                continue;   // ReadCallBody advanced _pos
            }

            // "label": C { ... }   (precondition, may span multiple lines)
            if (line.Contains("C {") && !line.Contains("Call"))
            {
                string? label = null;
                var lm = Regex.Match(line, @"""([^""]+)""\s*:");
                if (lm.Success) label = lm.Groups[1].Value;

                // Read the full (possibly multi-line) C block and apply it.
                string cBody = ReadCallBody();   // advances _pos past the statement
                var cm = Regex.Match(cBody, @"C\s*\{(.+)\}", RegexOptions.Singleline);
                if (cm.Success) ApplyAssignments(cm.Groups[1].Value, cur);

                // The reference tool merges the immediately-following setup macro
                // into the precondition, so peek ahead and apply its state + WFT
                // before emitting (the macro line is still processed normally and
                // re-applying the same state is idempotent).
                int look = _pos;
                while (look < _lines.Length)
                {
                    string nxt = _lines[look].Trim();
                    if (nxt.Length == 0 || nxt.StartsWith("Ann")) { look++; continue; }
                    var mp = Regex.Match(nxt, @"^Macro\s+""([^""]+)""");
                    if (mp.Success) ExpandMacro(mp.Groups[1].Value, cur, ref curWFT, pat);
                    break;
                }

                // precondition produces 2 identical lines
                int preOrd = ++_labelOrdinal;
                string preLabel = DedupLabel(label ?? "precondition all Signals", preOrd);
                Emit(pat, MakeRow(cur, curWFT, preLabel));
                Emit(pat, MakeRow(cur, null, null));
                continue;   // ReadCallBody already advanced _pos
            }

            // "pattern N": Call "proc" { � }  or  Call "proc" { � }
            if (line.Contains("Call "))
            {
                string? label = null;
                var lm = Regex.Match(line, @"""([^""]+)""\s*:\s*Call");
                if (lm.Success) label = lm.Groups[1].Value;

                var cm = Regex.Match(line, @"Call\s+""([^""]+)""");
                string procName = cm.Groups[1].Value;

                // read the full call body (may span many lines)
                string callBody = ReadCallBody();

                if (_procedures.TryGetValue(procName, out var proc))
                {
                    if (proc.HasShift)
                        ExpandLoadUnload(proc, callBody, cur, ref curWFT, pat, label);
                    else
                        ExpandCapture(proc, callBody, cur, ref curWFT, pat, label);
                }
                continue;   // ReadCallBody already advanced _pos
            }

            // Any remaining statement that carries a leading keyword is a command
            // this tool does not recognize. Record it (with its 1-based source
            // line) and emit a marker row so the generated pattern carries a
            // conspicuous mark; the caller logs "unknown command \"XXX\" at line ##".
            // Blank lines and bare braces are structural and skipped silently.
            if (line.Length > 0)
            {
                string stmt = line;
                var lbl = Regex.Match(stmt, @"^""[^""]+""\s*:\s*");
                if (lbl.Success) stmt = stmt[lbl.Length..];

                var cmd = Regex.Match(stmt, @"^([A-Za-z_]\w*)");
                if (cmd.Success)
                {
                    int lineNo = _pos + 1;
                    string command = cmd.Groups[1].Value;
                    pat.UnknownCommands.Add((lineNo, command));
                    Emit(pat, new VectorRow
                    {
                        UnknownCommand = command,
                        UnknownCommandLine = lineNo
                    });
                    SkipUnknownStatement();
                    continue;
                }
            }

            _pos++;
        }
    }

    /// <summary>Advance <see cref="_pos"/> past an unrecognized statement so its
    /// continuation lines are not re-flagged as further unknown commands. A brace
    /// block is consumed up to its matching close; otherwise lines are consumed up
    /// to the <c>;</c> terminator (falling back to a single line).</summary>
    private void SkipUnknownStatement()
    {
        int depth = 0;
        bool sawBrace = false;
        while (_pos < _lines.Length)
        {
            string l = _lines[_pos];
            foreach (char c in l)
            {
                if (c == '{') { depth++; sawBrace = true; }
                else if (c == '}') depth--;
            }
            _pos++;
            if (sawBrace) { if (depth <= 0) break; }   // brace block fully consumed
            else if (l.Contains(';')) break;           // statement terminator
            else break;                                // bare single line
        }
    }

    private void ExpandMacro(string name, Dictionary<string, char> cur,
        ref string curWFT, PatternInfo pat)
    {
        if (!_macroDefs.TryGetValue(name, out var mac)) return;

        if (!string.IsNullOrEmpty(mac.WFT)) curWFT = mac.WFT;

        // apply C
        var cLine = Regex.Match(mac.Body, @"C\s*\{(.+?)\}", RegexOptions.Singleline);
        if (cLine.Success) ApplyAssignments(cLine.Groups[1].Value, cur);

        // apply V statements (each produces a vector line but they are *merged*
        // into the precondition in the reference tool, so we just apply state)
        foreach (Match vm in Regex.Matches(mac.Body, @"V\s*\{([^}]*)\}"))
        {
            string inner = vm.Groups[1].Value.Trim();
            if (inner.Length > 0) ApplyAssignments(inner, cur);
        }
    }

    /// <summary>Expand a macro invoked with an inline body that overrides its V
    /// assignments (e.g. <c>Macro "capture" { PI_grp_0 = 0110; _po_ = X; }</c>).
    /// Applies the macro's own condition/waveform, then the caller-supplied
    /// assignments, and emits one capture vector.</summary>
    private void ExpandMacroCapture(ProcDef mac, string body,
        Dictionary<string, char> cur, ref string curWFT, PatternInfo pat,
        string? label, ref bool pendingIddq)
    {
        if (!string.IsNullOrEmpty(mac.WFT)) curWFT = mac.WFT;

        // apply the macro definition's C condition first
        var cLine = Regex.Match(mac.Body, @"C\s*\{(.+?)\}", RegexOptions.Singleline);
        if (cLine.Success) ApplyAssignments(cLine.Groups[1].Value, cur);

        // then the caller-supplied inline assignments (strip the "Macro "name"" and
        // outer braces, leaving the body assignments).
        var inner = Regex.Match(body, @"\{(.*)\}", RegexOptions.Singleline);
        if (inner.Success) ApplyAssignments(inner.Groups[1].Value, cur);

        bool iddq = pendingIddq || mac.Body.Contains("IddqTestPoint");
        pendingIddq = false;

        var row = MakeRow(cur, curWFT, label);
        row.IddqTestPoint = iddq;
        Emit(pat, row);
    }

    private void ExpandLoadUnload(ProcDef proc, string callBody,
        Dictionary<string, char> cur, ref string curWFT, PatternInfo pat,
        string? patternLabel)
    {
        string shiftWFT = proc.WFT;
        if (string.IsNullOrEmpty(shiftWFT)) shiftWFT = "_default_WFT_";
        curWFT = shiftWFT;

        // apply procedure C
        var cLine = Regex.Match(proc.Body, @"C\s*\{(.+?)\}", RegexOptions.Singleline);
        if (cLine.Success) ApplyAssignments(cLine.Groups[1].Value, cur);

        // apply procedure V before shift (e.g. Internal_scan_pre_shift)
        var preV = Regex.Match(proc.Body, @"V\s*\{([^}]+)\}");
        if (preV.Success) ApplyAssignments(preV.Groups[1].Value, cur);

        // parse shift V to know clk pattern
        var shiftV = Regex.Match(proc.Body,
            @"Shift\s*\{[^}]*V\s*\{([^}]+)\}", RegexOptions.Singleline);
        var clkPattern = new Dictionary<string, char>();
        if (shiftV.Success)
        {
            foreach (Match a in Regex.Matches(shiftV.Groups[1].Value,
                @"(?:""([^""]+)""|([A-Za-z_]\w*))\s*=\s*([^;""}\s]+)"))
            {
                string grp = a.Groups[1].Success ? a.Groups[1].Value : a.Groups[2].Value;
                string val = a.Groups[3].Value;
                if (_groups.TryGetValue(grp, out var members))
                {
                    // Group assignment: distribute the WFC string across members.
                    // A '#' marks scan-data-driven bits (handled separately below).
                    for (int k = 0; k < Math.Min(val.Length, members.Count); k++)
                        if (val[k] != '#') clkPattern[members[k]] = val[k];
                }
                else if (val.Length == 1 && val[0] != '#')
                {
                    // Bare signal held at a static level during every shift cycle,
                    // e.g. the scan clock "A2D_SCLK_SDA = 1" that pulses each shift.
                    clkPattern[grp] = val[0];
                }
            }
        }

        // extract scan data from call body
        var scanData = ParseScanData(callBody);

        // find SI and SO signals
        var siSigs = Grp("_si");
        var soSigs = Grp("_so");

        // The scan-data blocks may be keyed either by the member signal name or by
        // the scan group's own name (e.g. "_chain1_A2D_SCAN_SDI_I_"). Try both.
        string siData = "", soData = "";
        foreach (var key in siSigs.Concat(_siGroupNames))
            if (scanData.TryGetValue(key, out var d) && d.Length > siData.Length) siData = d;
        foreach (var key in soSigs.Concat(_soGroupNames))
            if (scanData.TryGetValue(key, out var d) && d.Length > soData.Length) soData = d;

        // When an unload provides only scan-out data (no scan-in), the scan-input
        // pin is driven low during the shift rather than left tri-stated.
        bool siDriven = siData.Length > 0;

        int len = Math.Max(siData.Length, soData.Length);

        // The "pattern N" call label and the procedure's own labeled statement
        // (e.g. "Internal_scan_pre_shift") both advance the global label ordinal,
        // even though only the call label is emitted on the pre-shift vector.
        int patternOrd = ++_labelOrdinal;                 // "pattern N" call label
        string? labelStr = patternLabel != null
            ? DedupLabel(patternLabel, patternOrd) : null;
        foreach (Match _ in Regex.Matches(proc.Body, @"""([^""]+)""\s*:\s*V\s*\{"))
            _labelOrdinal++;                              // internal proc label(s)

        // first vector: pre-shift state
        Emit(pat, MakeRow(cur, curWFT, labelStr));

        // shift vectors
        for (int bit = 0; bit < len; bit++)
        {
            // apply clk pattern
            foreach (var kv in clkPattern) cur[kv.Key] = kv.Value;

            // SI data
            foreach (var si in siSigs)
            {
                if (bit < siData.Length) cur[si] = siData[bit];
                else if (!siDriven) cur[si] = '0';
            }

            // SO data
            foreach (var so in soSigs)
                if (bit < soData.Length) cur[so] = soData[bit];

            Emit(pat, MakeRow(cur, null, null));
        }
    }

    private void ExpandCapture(ProcDef proc, string callBody,
        Dictionary<string, char> cur, ref string curWFT, PatternInfo pat,
        string? patternLabel)
    {
        string wft = proc.WFT;
        if (string.IsNullOrEmpty(wft)) wft = curWFT;

        // apply C
        var cLine = Regex.Match(proc.Body, @"C\s*\{(.+?)\}", RegexOptions.Singleline);
        if (cLine.Success) ApplyAssignments(cLine.Groups[1].Value, cur);

        // apply F
        foreach (Match fm in Regex.Matches(proc.Body, @"F\s*\{([^}]+)\}"))
            ApplyAssignments(fm.Groups[1].Value, cur);

        // Inline call arguments (e.g. "_pi"=...; "_po"=...;) keyed by group.
        var args = ParseInlineArgs(callBody);

        // Labeled V statements inside the capture procedure produce one tester
        // vector each (e.g. "forcePI", "measurePO measureIDDQ"). The label drives
        // the force/measure action marker that the generator inserts.
        var labeledV = Regex.Matches(proc.Body,
            @"""([^""]+)""\s*:\s*V\s*\{([^}]*)\}");

        if (labeledV.Count > 0)
        {
            // Scan-input pins become the force/measure marker "M" when they are
            // tri-stated (Z) during an iddq force/measure vector.
            var siSigs = Grp("_si");
            bool first = true;
            int prevEnd = 0;   // end of the previous labeled V in the body text
            // Both capture labels (force / measure) advance the global ordinal but
            // share the first label's ordinal when a duplicate suffix is needed.
            int captureBaseOrd = _labelOrdinal + 1;
            foreach (Match v in labeledV)
            {
                _labelOrdinal++;   // every labeled V advances the global count
                // Apply the call data for whichever group this V references.
                // Output-compare data (the "_po" group) is not compared during an
                // iddq force/measure point, so the precondition mask (_po = X set
                // by the procedure C statement) is kept instead.
                foreach (Match g in Regex.Matches(v.Groups[2].Value, @"""([^""]+)"""))
                {
                    string grp = g.Groups[1].Value;
                    if (grp == "_po") continue;
                    if (args.TryGetValue(grp, out var data))
                        ApplyGroupOrSignal(grp, data, cur);
                }

                var rowState = new Dictionary<string, char>(cur);
                foreach (var si in siSigs)
                    if (rowState.TryGetValue(si, out var c) && (c == 'Z' || c == 'T'))
                        rowState[si] = 'M';

                // An "IddqTestPoint;" statement that appears in the body between the
                // previous labeled V and this one marks an IDDQ measure vector; flag
                // the row so the generator emits the "// IddqTestPoint at cycle N"
                // comment the reference tool produces.
                string gap = v.Index > prevEnd
                    ? proc.Body.Substring(prevEnd, v.Index - prevEnd)
                    : "";
                bool iddq = Regex.IsMatch(gap, @"\bIddqTestPoint\b");

                string label = DedupLabel(v.Groups[1].Value, captureBaseOrd);
                Emit(pat, new VectorRow
                {
                    TimeSet = first ? wft : "-",
                    Values = rowState,
                    Label = label,
                    IddqTestPoint = iddq
                });
                first = false;
                prevEnd = v.Index + v.Length;
            }
        }
        else
        {
            // Unlabeled capture (e.g. multiclock_capture): single vector.
            ApplyCallArgs(callBody, cur);

            // A bidirectional scan-control pin that is not driven during capture
            // (its WFC is a compare-off / tri-state state, T or Z) is measured by
            // the tester and rendered with the force/measure marker "M". Clocks
            // (the _clk group) keep their resolved level and are handled below.
            var clkSet = new HashSet<string>(Grp("_clk"));
            var measured = new HashSet<string>(Grp("_si"));
            foreach (var io in Grp("_io"))
                if (!clkSet.Contains(io)) measured.Add(io);
            foreach (var sig in measured)
                if (cur.TryGetValue(sig, out var c) && (c == 'T' || c == 'Z'))
                    cur[sig] = 'M';

            // Resolve a pulse (P) on each clock/launch pin to the static level the
            // tester applies for that waveform: the pulse's active (mid) edge. A
            // clock idle-low/pulse-high renders 1, an idle-high/pulse-low renders 0.
            foreach (var clk in clkSet)
                if (cur.TryGetValue(clk, out var c) && c == 'P')
                    cur[clk] = PulseLevel(clk, wft);

            Emit(pat, MakeRow(cur, wft, null));
        }

        curWFT = wft;
    }

    /// <summary>Parse inline "group"=value; assignments from a call body (the
    /// group name may be quoted or bare).</summary>
    private static Dictionary<string, string> ParseInlineArgs(string body)
    {
        var result = new Dictionary<string, string>();
        foreach (Match m in Regex.Matches(body,
            @"(?:""([^""]+)""|([A-Za-z_]\w*))\s*=\s*([0-9A-Za-z#]+)\s*;"))
            result[m.Groups[1].Success ? m.Groups[1].Value : m.Groups[2].Value] = m.Groups[3].Value;
        return result;
    }

    /// <summary>Return the unique form of a label name. The first use of a name
    /// is kept verbatim; any later use is suffixed with "_N" where N is the
    /// supplied ordinal, matching the reference tool's de-duplication. Ordinal
    /// bookkeeping (<see cref="_labelOrdinal"/>) is managed by the caller so the
    /// running count includes labels that are consumed but not emitted.</summary>
    private string DedupLabel(string name, int suffixOrdinal)
    {
        string flat = name.Replace(" ", "");
        return _seenLabels.Add(flat) ? flat : $"{flat}_{suffixOrdinal}";
    }

    // ?? helpers ?????????????????????????????????????????????????????

    /// <summary>Read the call body from the current _pos (which is on the Call line)
    /// up to the matching closing brace, advancing _pos past it.</summary>
    private string ReadCallBody()
    {
        var sb = new StringBuilder();
        int depth = 0;
        bool started = false;
        while (_pos < _lines.Length)
        {
            string l = _lines[_pos];
            foreach (char c in l)
            {
                if (c == '{') { depth++; started = true; }
                if (c == '}') depth--;
            }
            sb.AppendLine(l);
            _pos++;
            if (started && depth <= 0) break;
        }
        return sb.ToString();
    }

    /// <summary>Parse long scan-data strings from a call body. Each entry is of
    /// the form <c>name = DATA ;</c> where the name may be quoted or bare and the
    /// data (possibly spanning many lines) is a WFC string that may contain
    /// repeat tokens (<c>\rN C</c>, <c>\j</c>). Returns the fully expanded data.</summary>
    private Dictionary<string, string> ParseScanData(string body)
    {
        var result = new Dictionary<string, string>();
        string? curSig = null;
        var raw = new StringBuilder();

        void Flush()
        {
            if (curSig != null)
                result[curSig] = ExpandRepeat(raw.ToString());
            curSig = null;
            raw.Clear();
        }

        // A scan-data payload is made up of WFC characters, whitespace and repeat
        // tokens (backslash sequences / digits) only. This guards against treating
        // ordinary inline call arguments (e.g. "_pi"=1101...ZZZZZ; "_po"=HTTTTT;)
        // that pack several assignments and punctuation on one line as scan data.
        static bool IsScanPayload(string s) =>
            s.Length > 0 && Regex.IsMatch(s, @"^[01UDZHLTXPN\\rj\s\d]+$");

        foreach (var lineRaw in body.Split('\n'))
        {
            string l = lineRaw.Trim();

            // The first scan assignment may share the line with the call/macro
            // opener, e.g.  Macro "load_unload_grp1" { "_chain1_..._" = 1110... .
            // Strip a leading "Macro/Call "name"" and any opening braces so the
            // assignment that follows is recognized.
            l = Regex.Replace(l, @"^(?:Macro|Call)\s+""[^""]+""\s*", "");
            l = l.TrimStart('{', ' ', '\t');

            // Start of an assignment: name = <optional data on same line>
            var start = Regex.Match(l,
                @"^(?:""([^""]+)""|([A-Za-z_]\w*))\s*=\s*(.*)$");
            if (start.Success)
            {
                Flush();
                string name = start.Groups[1].Success ? start.Groups[1].Value : start.Groups[2].Value;
                string rest = start.Groups[3].Value;
                int semi = rest.IndexOf(';');
                bool done = semi >= 0;
                // Data is everything up to the terminator; trailing braces / spaces
                // from a same-line "... ; }" are stripped.
                string payload = (done ? rest[..semi] : rest).Trim().TrimEnd('}', '{', ' ');

                // Only begin capturing when the remainder looks like scan data (or
                // is empty, meaning the data follows on subsequent lines).
                if (payload.Length == 0 || IsScanPayload(payload))
                {
                    curSig = name;
                    raw.Append(' ').Append(payload);
                    if (done) Flush();
                }
                continue;
            }

            if (curSig != null)
            {
                int semi = l.IndexOf(';');
                bool done = semi >= 0;
                string payload = (done ? l[..semi] : l).Trim().TrimEnd('}', '{', ' ');
                if (IsScanPayload(payload)) raw.Append(' ').Append(payload);
                if (done) Flush();
            }
        }
        Flush();
        return result;
    }

    /// <summary>Apply inline call arguments like "_pi"=11P00; "_po"=LH; (names
    /// may be quoted or bare).</summary>
    private void ApplyCallArgs(string body, Dictionary<string, char> cur)
    {
        // find the closing-brace line that has inline assignments
        foreach (var line in body.Split('\n'))
        {
            foreach (Match m in Regex.Matches(line,
                @"(?:""([^""]+)""|([A-Za-z_]\w*))\s*=\s*([^;""}\s]+)"))
            {
                string grp = m.Groups[1].Success ? m.Groups[1].Value : m.Groups[2].Value;
                string val = m.Groups[3].Value;
                ApplyGroupOrSignal(grp, ExpandRepeat(val), cur);
            }
        }
    }

    private void ApplyCondition(string line, Dictionary<string, char> cur)
    {
        var cm = Regex.Match(line, @"C\s*\{(.+?)\}");
        if (cm.Success) ApplyAssignments(cm.Groups[1].Value, cur);
    }

    private void ApplyAssignments(string text, Dictionary<string, char> cur)
    {
        // handle multiline (join)
        text = text.Replace('\n', ' ').Replace('\r', ' ');
        // Name may be quoted ("_pi") or bare (PI_grp_0). Value is a WFC token
        // string, possibly space-separated repeat tokens (\j, \rN C).
        foreach (Match m in Regex.Matches(text,
            @"(?:""([^""]+)""|([A-Za-z_]\w*))\s*=\s*([^;""}\s]+(?:\s+[^;""}\s]+)*)"))
        {
            string grp = m.Groups[1].Success ? m.Groups[1].Value : m.Groups[2].Value;
            string rawVal = m.Groups[3].Value;
            ApplyGroupOrSignal(grp, ExpandRepeat(rawVal), cur);
        }
    }

    private void ApplyGroupOrSignal(string name, string val, Dictionary<string, char> cur)
    {
        if (_groups.TryGetValue(name, out var members))
        {
            int idx = 0;
            for (int k = 0; k < members.Count && idx < val.Length; k++)
            {
                char c = val[idx++];
                if (c != '#') cur[members[k]] = c;
            }
        }
        else if (cur.ContainsKey(name) && val.Length >= 1 && val[0] != '#')
        {
            cur[name] = val[0];
        }
    }

    /// <summary>Resolve the static level the tester applies for a pulse (P) on a
    /// given signal in a capture vector. A pulse is defined by three edges in the
    /// WaveformTable (idle, active, idle); the rendered value is the active (mid)
    /// edge: an idle-low/pulse-high clock renders 1, an idle-high/pulse-low one
    /// renders 0. Falls back to 1 when no waveform definition is found.</summary>
    private char PulseLevel(string signal, string wftName)
    {
        var ts = _timeSets.FirstOrDefault(t => t.Name == wftName);
        if (ts != null)
        {
            var wf = ts.Waveforms.FirstOrDefault(w =>
                w.WFC == 'P' &&
                (w.SignalOrGroup == signal ||
                 (_groups.TryGetValue(w.SignalOrGroup, out var g) && g.Contains(signal))));
            if (wf != null && wf.Edges.Count >= 2)
            {
                // active (mid) edge: U -> 1, D -> 0
                char act = wf.Edges[wf.Edges.Count / 2].Action;
                if (act == 'U') return '1';
                if (act == 'D') return '0';
            }
        }
        return '1';
    }

    private static string ExpandRepeat(string s)
    {
        // \j = keep current (empty), \rN C = repeat C N times
        s = Regex.Replace(s, @"\\j\s*", "");
        s = Regex.Replace(s, @"\\r(\d+)\s+(\S)", m =>
            new string(m.Groups[2].Value[0], int.Parse(m.Groups[1].Value)));
        // A WFC value is a positional string with one character per signal; the
        // token separators left over from \rN expansion would otherwise shift
        // every subsequent signal by one column, so strip all whitespace.
        return Regex.Replace(s, @"\s+", "");
    }

    private VectorRow MakeRow(Dictionary<string, char> cur, string? wft, string? label)
    {
        return new VectorRow
        {
            TimeSet = wft ?? "-",
            Values = new Dictionary<string, char>(cur),
            Label = label
        };
    }

    // ?? block reader ???????????????????????????????????????????????
    private string ReadBlock()
    {
        var sb = new StringBuilder();
        int depth = 0; bool started = false;
        while (_pos < _lines.Length)
        {
            string l = _lines[_pos];
            foreach (char c in l)
            {
                if (c == '{') { depth++; started = true; }
                if (c == '}') depth--;
            }
            if (started) sb.AppendLine(l);
            _pos++;
            if (started && depth <= 0) break;
        }
        return sb.ToString();
    }

    private static double ParseTime(string s)
    {
        s = s.Trim();
        if (s.EndsWith("ns")) return double.Parse(s[..^2]) * 1e-9;
        if (s.EndsWith("us")) return double.Parse(s[..^2]) * 1e-6;
        if (s.EndsWith("ms")) return double.Parse(s[..^2]) * 1e-3;
        if (s.EndsWith('s'))  return double.Parse(s[..^1]);
        return double.Parse(s);
    }

    // ?? tiny helpers ???????????????????????????????????????????????
    private record StilSig(string Name, string Dir, string Scan);
    private class ProcDef
    {
        public string Name { get; set; } = "";
        public string Body { get; set; } = "";
        public string WFT  { get; set; } = "";
        public bool HasShift { get; set; }
    }
}
