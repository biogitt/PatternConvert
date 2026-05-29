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
}
