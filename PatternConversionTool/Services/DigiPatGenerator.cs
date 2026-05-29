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
        // build mapping: STIL name ? output pin name
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
            if (row.TimeSet != "-")
                sb.Append(row.TimeSet.PadRight(32));
            else
                sb.Append("-".PadRight(32));

            foreach (var sig in enabledSignals)
            {
                char val = row.Values.TryGetValue(sig.OriginalStilName, out var v) ? v : 'X';
                // N (no-change) renders as X in the output
                if (val == 'N') val = 'X';
                // Z for bidir also renders as X in shift context unless it's really tri-state
                sb.Append(val);
                sb.Append(' ');
            }
            sb.Append(';');
            w.WriteLine(sb.ToString().TrimEnd());
        }

        w.WriteLine("}");
    }
}
