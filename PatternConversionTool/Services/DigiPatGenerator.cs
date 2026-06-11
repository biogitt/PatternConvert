using System.IO;
using System.Text;
using PatternConversionTool.Models;

namespace PatternConversionTool.Services;

/// <summary>Generates the .digipatsrc text pattern file.</summary>
public class DigiPatGenerator
{
    public void Generate(string outputPath, PatternInfo pattern,
        IReadOnlyList<Signal> enabledSignals, string? sourceFile = null)
    {
        // build mapping: STIL name -> output pin name
        var stilToPin = enabledSignals.ToDictionary(
            s => s.OriginalStilName, s => s.Name);

        using var w = new StreamWriter(outputPath, false, new UTF8Encoding(false));

        // header
        w.WriteLine();
        w.WriteLine("// " + new string('-', 88));
        w.WriteLine("// PatternConversionTool");
        if (sourceFile != null)
            w.WriteLine($"// Source file {sourceFile}");
        w.WriteLine($"// Converted on {DateTime.Now:MMMM d, yyyy} at {DateTime.Now:HH:mm:ss}");
        w.WriteLine("// " + new string('-', 88));
        w.WriteLine();

        // file_format_version
        w.WriteLine("file_format_version 1.1;");

        // timeset
        w.WriteLine($"timeset {string.Join(", ", pattern.TimeSetOrder)};");
        w.WriteLine();

        // pattern header
        var pinNames = enabledSignals.Select(s => s.Name).ToList();
        w.WriteLine($"pattern {pattern.PatternName} ({string.Join(", ", pinNames)})");
        w.WriteLine("{");

        // vectors
        // The tester format prints a timeset name only when it differs from the
        // previously emitted one; an unchanged timeset is written as "-". A row
        // whose TimeSet is already "-" carries no change by construction.
        // The final data vector is prefixed with the end-of-pattern "halt" marker.
        int lastDataIndex = -1;
        for (int i = pattern.Vectors.Count - 1; i >= 0; i--)
        {
            var rw = pattern.Vectors[i];
            if (!(rw.Comment != null && rw.Values.Count == 0)) { lastDataIndex = i; break; }
        }
        // Only mark the terminal vector when the whole pattern was expanded; a
        // capped / truncated parse must not emit a premature halt.
        if (!pattern.IsComplete) lastDataIndex = -1;

        string lastTimeSet = "";
        int vectorCount = 0;   // running count of emitted data vectors
        for (int i = 0; i < pattern.Vectors.Count; i++)
        {
            var row = pattern.Vectors[i];
            // comment-only row
            if (row.Comment != null && row.Values.Count == 0)
            {
                w.WriteLine($"// Ann {{* {row.Comment} *}}");
                continue;
            }

            // An IDDQ measure vector is preceded by a "// IddqTestPoint at cycle N"
            // comment, where N is the number of vectors emitted before it.
            if (row.IddqTestPoint)
                w.WriteLine($"// IddqTestPoint at cycle {vectorCount}");

            // label
            if (row.Label != null)
                w.WriteLine($"{row.Label}:");

            // timeset + data. The 25-column indent is replaced by the left-aligned
            // "halt" keyword on the very last vector of the pattern.
            var sb = new StringBuilder();
            sb.Append(i == lastDataIndex ? "halt".PadRight(25) : new string(' ', 25));
            string tsToken;
            if (row.TimeSet == "-" || row.TimeSet == lastTimeSet)
            {
                tsToken = "-";
            }
            else
            {
                tsToken = row.TimeSet;
                lastTimeSet = row.TimeSet;
            }
            sb.Append(tsToken.PadRight(32));

            foreach (var sig in enabledSignals)
            {
                char val = row.Values.TryGetValue(sig.OriginalStilName, out var v) ? v : 'X';
                sb.Append(NormalizeSymbol(val));
                sb.Append(' ');
            }
            if (sb.Length > 0 && sb[^1] == ' ') sb.Length--;   // no space before ';'
            sb.Append(';');
            w.WriteLine(sb.ToString());
            vectorCount++;
        }

        w.WriteLine("}");
    }

    /// <summary>Map a STIL waveform character to the reduced tester symbol set
    /// (0 1 H L M X). Pulse (P) renders as a static 1; tri-state / no-change /
    /// compare-off states (Z, T, N) render as don't-care X. The force/measure
    /// marker M is produced by the parser and passes through unchanged.</summary>
    private static char NormalizeSymbol(char c) => c switch
    {
        'P' => '1',                  // pulse clock -> static 1
        'Z' or 'T' or 'N' => 'X',    // tri-state / compare-off / no-change -> X
        _ => c,
    };
}
