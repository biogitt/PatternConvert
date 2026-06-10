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
        string lastTimeSet = "";
        foreach (var row in pattern.Vectors)
        {
            // comment-only row
            if (row.Comment != null && row.Values.Count == 0)
            {
                w.WriteLine($"// Ann {{* {row.Comment} *}}");
                continue;
            }

            // label
            if (row.Label != null)
                w.WriteLine($"{row.Label}:");

            // timeset + data
            var sb = new StringBuilder();
            sb.Append("                         ");
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
