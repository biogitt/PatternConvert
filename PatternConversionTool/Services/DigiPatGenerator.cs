using System.IO;
using System.Text;
using PatternConversionTool.Models;

namespace PatternConversionTool.Services;

/// <summary>Generates the .digipatsrc text pattern file.</summary>
public class DigiPatGenerator
{
    public void Generate(string outputPath, PatternInfo pattern,
        IReadOnlyList<Signal> enabledSignals, string? sourceFile = null,
        IProgress<(int current, int total)>? progress = null)
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

        var state = new WriteState();
        int total = pattern.Vectors.Count;
        for (int i = 0; i < pattern.Vectors.Count; i++)
        {
            // Report progress (throttled) so the UI can show "line xxx/xxxx".
            if (progress != null && (i % 2000 == 0 || i == total - 1))
                progress.Report((i + 1, total));

            WriteRow(w, pattern.Vectors[i], enabledSignals, state, i == lastDataIndex);
        }

        progress?.Report((total, total));
        w.WriteLine("}");
    }

    /// <summary>Streaming counterpart of <see cref="Generate"/>: parses the STIL
    /// file and writes each expanded vector row straight to disk, so memory stays
    /// flat regardless of pattern size. The <paramref name="parse"/> callback is
    /// invoked with a row sink that this method wires into the file writer; it
    /// returns whether the pattern was fully expanded (controls the end-of-pattern
    /// "halt" marker).</summary>
    public void GenerateStreaming(string outputPath, PatternInfo patternMeta,
        IReadOnlyList<Signal> enabledSignals,
        Func<Action<VectorRow>, bool> parse, string? sourceFile = null,
        IProgress<(int current, int total)>? progress = null)
    {
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

        w.WriteLine("file_format_version 1.1;");
        w.WriteLine($"timeset {string.Join(", ", patternMeta.TimeSetOrder)};");
        w.WriteLine();

        var pinNames = enabledSignals.Select(s => s.Name).ToList();
        w.WriteLine($"pattern {patternMeta.PatternName} ({string.Join(", ", pinNames)})");
        w.WriteLine("{");

        // Streaming cannot look ahead to find the final data row, so a single-row
        // delay buffer holds the previous data vector: the held row is written
        // normally once another data row arrives, and the very last one is written
        // with the "halt" marker after the stream ends. Comment-only rows are not
        // candidates for halt and are written immediately.
        var state = new WriteState();
        long total = Math.Max(patternMeta.EstimatedCycles, 1);
        long count = 0;
        VectorRow? pendingData = null;

        bool complete = parse(row =>
        {
            bool isComment = row.Comment != null && row.Values.Count == 0;
            if (isComment)
            {
                // Flush any held data row first to preserve order.
                if (pendingData != null) { WriteRow(w, pendingData, enabledSignals, state, false); pendingData = null; }
                WriteRow(w, row, enabledSignals, state, false);
                return;
            }

            // Data row: flush the previously held one (never the last), hold this.
            if (pendingData != null)
                WriteRow(w, pendingData, enabledSignals, state, false);
            pendingData = row;

            count++;
            if (progress != null && count % 2000 == 0)
                progress.Report(((int)Math.Min(count, int.MaxValue), (int)Math.Min(total, int.MaxValue)));
        });

        // Write the final held data row, marking it as halt when the pattern was
        // fully expanded.
        if (pendingData != null)
            WriteRow(w, pendingData, enabledSignals, state, complete);

        progress?.Report(((int)Math.Min(count, int.MaxValue), (int)Math.Min(count, int.MaxValue)));
        w.WriteLine("}");
    }

    /// <summary>Mutable per-file writer state shared across rows.</summary>
    private sealed class WriteState
    {
        public string LastTimeSet = "";
        public int VectorCount;       // running count of emitted data vectors
    }

    /// <summary>Format and write a single row (comment, label, timeset + data).
    /// When <paramref name="isLast"/> is true the 25-column indent is replaced by
    /// the left-aligned end-of-pattern "halt" keyword.</summary>
    private void WriteRow(StreamWriter w, VectorRow row,
        IReadOnlyList<Signal> enabledSignals, WriteState state, bool isLast)
    {
        // comment-only row
        if (row.Comment != null && row.Values.Count == 0)
        {
            w.WriteLine($"// Ann {{* {row.Comment} *}}");
            return;
        }

        // An IDDQ measure vector is preceded by a "// IddqTestPoint at cycle N"
        // comment, where N is the number of vectors emitted before it.
        if (row.IddqTestPoint)
            w.WriteLine($"// IddqTestPoint at cycle {state.VectorCount}");

        // label
        if (row.Label != null)
            w.WriteLine($"{row.Label}:");

        // timeset + data. The 25-column indent is replaced by the left-aligned
        // "halt" keyword on the very last vector of the pattern.
        var sb = new StringBuilder();
        sb.Append(isLast ? "halt".PadRight(25) : new string(' ', 25));
        string tsToken;
        if (row.TimeSet == "-" || row.TimeSet == state.LastTimeSet)
        {
            tsToken = "-";
        }
        else
        {
            tsToken = row.TimeSet;
            state.LastTimeSet = row.TimeSet;
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
        state.VectorCount++;
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
