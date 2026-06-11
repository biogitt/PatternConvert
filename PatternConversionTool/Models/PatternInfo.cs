namespace PatternConversionTool.Models;

/// <summary>
/// Holds the parsed pattern data from the STIL file, ready for generation.
/// </summary>
public class PatternInfo
{
    public string PatternName { get; set; } = "_pattern_";

    /// <summary>Ordered timeset names as declared in the STIL file.</summary>
    public List<string> TimeSetOrder { get; set; } = new();

    /// <summary>Flat list of expanded vector rows.</summary>
    public List<VectorRow> Vectors { get; set; } = new();

    /// <summary>True when expansion reached the natural end of the Pattern block
    /// (as opposed to being truncated by a vector cap). The generator only emits
    /// the end-of-pattern <c>halt</c> marker when the pattern is complete.</summary>
    public bool IsComplete { get; set; }

    /// <summary>Approximate number of test cycles, read cheaply from the STIL
    /// footer ("generating N test cycles"). Used as a progress-bar denominator
    /// when streaming the pattern, since the exact total is not known up front.
    /// Zero when the file carries no such annotation.</summary>
    public long EstimatedCycles { get; set; }
}

/// <summary>
/// One row of the output digipatsrc file.
/// </summary>
public class VectorRow
{
    /// <summary>Timeset name, or "-" to repeat previous.</summary>
    public string TimeSet { get; set; } = "-";

    /// <summary>Per-signal values keyed by STIL signal name. Char can be 0/1/X/H/L/T/Z/N/P.</summary>
    public Dictionary<string, char> Values { get; set; } = new();

    /// <summary>Optional label printed before this row (e.g. "pattern0:").</summary>
    public string? Label { get; set; }

    /// <summary>Optional comment printed before this row.</summary>
    public string? Comment { get; set; }

    /// <summary>True when an STIL <c>IddqTestPoint;</c> marker precedes this row.
    /// The generator emits a <c>// IddqTestPoint at cycle N</c> comment before the
    /// row's label, where N is the running count of vectors emitted so far.</summary>
    public bool IddqTestPoint { get; set; }
}
