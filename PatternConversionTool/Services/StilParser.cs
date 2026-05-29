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

    // ?? public entry point ??????????????????????????????????????????
    public StilParseResult Parse(string filePath)
    {
        _lines = File.ReadAllLines(filePath);
        _pos = 0;

        while (_pos < _lines.Length)
        {
            string line = _lines[_pos].Trim();
            if (line.StartsWith("Signals"))      ParseSignals();
            else if (line.StartsWith("SignalGroups")) ParseSignalGroups();
            else if (line.StartsWith("Timing"))  ParseTiming();
            else if (line.StartsWith("Procedures")) ParseProcedures();
            else if (line.StartsWith("MacroDefs"))  ParseMacroDefs();
            else if (Regex.IsMatch(line, @"^Pattern\s+""")) return BuildResult(filePath);
            else _pos++;
        }
        return BuildResult(filePath);
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

    private StilParseResult BuildResult(string filePath)
    {
        var pattern = new PatternInfo
        {
            TimeSetOrder = new List<string>(_timeSetOrder)
        };

        // try to find Pattern block and expand it
        for (int i = 0; i < _lines.Length; i++)
        {
            if (Regex.IsMatch(_lines[i].Trim(), @"^Pattern\s+"""))
            {
                var nm = Regex.Match(_lines[i], @"""([^""]+)""");
                if (nm.Success) pattern.PatternName = nm.Groups[1].Value;
                _pos = i + 1;
                ExpandPattern(pattern);
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

    // ?? expand Pattern block into flat VectorRows ??????????????????
    private void ExpandPattern(PatternInfo pat)
    {
        // Per-signal current state (STIL signal names)
        var cur = new Dictionary<string, char>(); string? _pendingLabel = null;
        foreach (var s in _signals) cur[s.Name] = 'X';

        string curWFT = "_default_WFT_";

        while (_pos < _lines.Length)
        {
            string line = _lines[_pos].Trim();
            if (line == "}") break;
            if (line == "{") { _pos++; continue; }

            // W "wft";
            var wm = Regex.Match(line, @"^\s*W\s+""([^""]+)""");
            if (wm.Success) { curWFT = wm.Groups[1].Value; _pos++; continue; }

            // Ann {* � *}
            var am = Regex.Match(line, @"Ann\s*\{\*\s*(.+?)\s*\*\}");
            if (am.Success)
            {
                pat.Vectors.Add(new VectorRow { Comment = am.Groups[1].Value });
                _pos++; continue;
            }

            // Macro "name";
            var mm = Regex.Match(line, @"Macro\s+""([^""]+)""");
            if (mm.Success)
            {
                ExpandMacro(mm.Groups[1].Value, cur, ref curWFT, pat);
                _pos++; continue;
            }

            // "label": C { � }  or  C { � }   (precondition)
            if (line.Contains("C {") && !line.Contains("Call"))
            {
                string? label = null;
                var lm = Regex.Match(line, @"""([^""]+)""\s*:");
                if (lm.Success) label = lm.Groups[1].Value;

                ApplyCondition(line, cur);
                // precondition produces 2 identical lines
                string wft = curWFT;
                pat.Vectors.Add(MakeRow(cur, wft, label?.Replace(" ", "") ?? "preconditionallSignals"));
                pat.Vectors.Add(MakeRow(cur, null, null));
                _pos++; continue;
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

            _pos++;
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

        int len = Math.Max(siData.Length, soData.Length);

        // label row
        string? labelStr = patternLabel?.Replace(" ", "");

        // first vector: pre-shift state
        pat.Vectors.Add(MakeRow(cur, curWFT, labelStr));

        // shift vectors
        for (int bit = 0; bit < len; bit++)
        {
            // apply clk pattern
            foreach (var kv in clkPattern) cur[kv.Key] = kv.Value;

            // SI data
            foreach (var si in siSigs)
                if (bit < siData.Length) cur[si] = siData[bit];

            // SO data
            foreach (var so in soSigs)
                if (bit < soData.Length) cur[so] = soData[bit];

            pat.Vectors.Add(MakeRow(cur, null, null));
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

        // apply call arguments
        ApplyCallArgs(callBody, cur);

        pat.Vectors.Add(MakeRow(cur, wft, null));
        curWFT = wft;
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
                if (dl.Length > 0 && Regex.IsMatch(dl, @"^[01LHXTZN]+$"))
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

    private static string ExpandRepeat(string s)
    {
        // \j = keep current (empty), \rN C = repeat C N times
        s = Regex.Replace(s, @"\\j\s*", "");
        s = Regex.Replace(s, @"\\r(\d+)\s+(\S)", m =>
            new string(m.Groups[2].Value[0], int.Parse(m.Groups[1].Value)));
        return s.Trim();
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
