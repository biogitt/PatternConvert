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
            if (line.StartsWith("Signals"))      ParseSignals();
            else if (line.StartsWith("SignalGroups")) ParseSignalGroups();
            else if (line.StartsWith("Timing"))  ParseTiming();
            else if (line.StartsWith("Procedures")) ParseProcedures();
            else if (line.StartsWith("MacroDefs"))  ParseMacroDefs();
            else if (Regex.IsMatch(line, @"^Pattern\s+""")) return BuildResult(filePath, expandPattern);
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
    }

    // ?? Signals { � } ??????????????????????????????????????????????
    private void ParseSignals()
    {
        string block = ReadBlock();
        foreach (Match m in Regex.Matches(block,
            @"""([^""]+)""\s+(In|Out|InOut)\s*;?\s*(\{\s*(ScanIn|ScanOut)\s*;\s*\})?"))
        {
            _signals.Add(new StilSig(m.Groups[1].Value, m.Groups[2].Value,
                m.Groups[4].Success ? m.Groups[4].Value : ""));
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
            var gm = Regex.Match(lines[i].Trim(), @"""([^""]+)""\s*=\s*'");
            if (gm.Success)
            {
                string name = gm.Groups[1].Value;
                var sb = new StringBuilder(lines[i]);
                while (!lines[i].Contains(';') && i + 1 < lines.Length)
                    sb.Append(' ').Append(lines[++i]);

                string full = sb.ToString();
                var sec = Regex.Match(full, @"'(.+?)'");
                if (sec.Success)
                {
                    var raw = Regex.Matches(sec.Groups[1].Value, @"""([^""]+)""")
                        .Cast<Match>().Select(m => m.Groups[1].Value).ToList();

                    // resolve nested groups
                    var resolved = new List<string>();
                    foreach (var s in raw)
                        resolved.AddRange(_groups.TryGetValue(s, out var g) ? g : [s]);
                    _groups[name] = resolved;
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
            var wm = Regex.Match(lines[i].Trim(), @"WaveformTable\s+""([^""]+)""");
            if (wm.Success)
            {
                var ts = new TimeSetDef { Name = wm.Groups[1].Value };
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
                    var em = Regex.Match(l, @"""([^""]+)""\s*\{\s*(\w)\s*\{(.+?)\}\s*\}");
                    if (em.Success)
                    {
                        ts.Waveforms.Add(new WaveformDef
                        {
                            SignalOrGroup = em.Groups[1].Value,
                            WFC = em.Groups[2].Value[0],
                            Edges = ParseEdges(em.Groups[3].Value)
                        });
                    }
                    if (depth <= -1) break;
                }
                _timeSets.Add(ts);
            }
            i++;
        }
    }

    private List<EdgeDef> ParseEdges(string s)
    {
        return Regex.Matches(s, @"'([^']+)'\s+(\w)")
            .Cast<Match>()
            .Select(m => new EdgeDef
            {
                TimeSeconds = ParseTime(m.Groups[1].Value),
                Action = m.Groups[2].Value[0]
            }).ToList();
    }

    // ?? Procedures / MacroDefs ?????????????????????????????????????
    private void ParseProcedures() => ParseProcBlock(_procedures);
    private void ParseMacroDefs()  => ParseProcBlock(_macroDefs);

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
                pd.WFT = Regex.Match(pd.Body, @"W\s+""([^""]+)""").Groups[1].Value;
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
            if (Regex.IsMatch(_lines[i].Trim(), @"^Pattern\s+"""))
            {
                var nm = Regex.Match(_lines[i], @"""([^""]+)""");
                if (nm.Success) pattern.PatternName = nm.Groups[1].Value;

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

        while (_pos < _lines.Length)
        {
            if (MaxVectors > 0 && EmittedRows >= MaxVectors) break;

            string line = _lines[_pos].Trim();
            if (line == "}") { pat.IsComplete = true; break; }
            if (line == "{") { _pos++; continue; }

            // W "wft";
            var wm = Regex.Match(line, @"^\s*W\s+""([^""]+)""");
            if (wm.Success) { curWFT = wm.Groups[1].Value; _pos++; continue; }

            // Ann {* � *}
            var am = Regex.Match(line, @"Ann\s*\{\*\s*(.+?)\s*\*\}");
            if (am.Success)
            {
                Emit(pat, new VectorRow { Comment = am.Groups[1].Value });
                _pos++; continue;
            }

            // Macro "name";
            var mm = Regex.Match(line, @"Macro\s+""([^""]+)""");
            if (mm.Success)
            {
                ExpandMacro(mm.Groups[1].Value, cur, ref curWFT, pat);
                _pos++; continue;
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
            foreach (Match a in Regex.Matches(shiftV.Groups[1].Value, @"""([^""]+)""\s*=\s*([^;""}\s]+)"))
            {
                string grp = a.Groups[1].Value;
                string val = a.Groups[2].Value;
                if (_groups.TryGetValue(grp, out var members))
                {
                    for (int k = 0; k < Math.Min(val.Length, members.Count); k++)
                        if (val[k] != '#') clkPattern[members[k]] = val[k];
                }
            }
        }

        // extract scan data from call body
        var scanData = ParseScanData(callBody);

        // find SI and SO signals
        var siSigs = Grp("_si");
        var soSigs = Grp("_so");

        string siData = "", soData = "";
        foreach (var si in siSigs)
            if (scanData.TryGetValue(si, out var d)) siData = d;
        foreach (var so in soSigs)
            if (scanData.TryGetValue(so, out var d)) soData = d;

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

    /// <summary>Parse inline "group"=value; assignments from a call body.</summary>
    private static Dictionary<string, string> ParseInlineArgs(string body)
    {
        var result = new Dictionary<string, string>();
        foreach (Match m in Regex.Matches(body, @"""([^""]+)""\s*=\s*([0-9A-Za-z#]+)\s*;"))
            result[m.Groups[1].Value] = m.Groups[2].Value;
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

    /// <summary>Parse long scan-data strings from a call body.</summary>
    private Dictionary<string, string> ParseScanData(string body)
    {
        var result = new Dictionary<string, string>();
        var lines = body.Split('\n');
        string? curSig = null;
        StringBuilder? data = null;

        for (int i = 0; i < lines.Length; i++)
        {
            string l = lines[i].Trim();

            // Inline form on a single line: "signal"=DATA;  (the scan string can
            // be thousands of characters long but still fits one line). Capture it
            // directly so it is not mistaken for the "data follows" form below.
            var inline = Regex.Match(l, @"^""([^""]+)""\s*=\s*([01UDZHLTXPN]+)\s*;");
            if (inline.Success)
            {
                if (curSig != null && data != null)
                    result[curSig] = data.ToString();
                curSig = null; data = null;
                result[inline.Groups[1].Value] = inline.Groups[2].Value;
                continue;
            }

            // "signal"= at end of line (data follows)
            var sa = Regex.Match(l, @"""([^""]+)""\s*=\s*$");
            if (sa.Success)
            {
                if (curSig != null && data != null)
                    result[curSig] = data.ToString();
                curSig = sa.Groups[1].Value;
                data = new StringBuilder();
                continue;
            }

            if (curSig != null && data != null)
            {
                string dl = l.TrimEnd(';').Trim();
                // Accept all STIL waveform characters: 0/1, U/D (force up/down),
                // Z (force off), H/L (compare high/low), T (compare off),
                // X (compare unknown / don't-care), P (pulse), N (unknown input).
                if (dl.Length > 0 && Regex.IsMatch(dl, @"^[01UDZHLTXPN]+$"))
                    data.Append(dl);
                if (l.Contains(';'))
                {
                    result[curSig] = data.ToString();
                    curSig = null; data = null;
                }
            }
        }
        if (curSig != null && data != null)
            result[curSig] = data.ToString();
        return result;
    }

    /// <summary>Apply inline call arguments like "_pi"=11P00; "_po"=LH;</summary>
    private void ApplyCallArgs(string body, Dictionary<string, char> cur)
    {
        // find the closing-brace line that has inline assignments
        foreach (var line in body.Split('\n'))
        {
            foreach (Match m in Regex.Matches(line, @"""([^""]+)""\s*=\s*([^;""}\s]+)"))
            {
                string grp = m.Groups[1].Value;
                string val = m.Groups[2].Value;
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
        foreach (Match m in Regex.Matches(text, @"""([^""]+)""\s*=\s*([^;""}\s]+(?:\s+[^;""}\s]+)*)"))
        {
            string grp = m.Groups[1].Value;
            string rawVal = m.Groups[2].Value;
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
