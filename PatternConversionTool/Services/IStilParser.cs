using PatternConversionTool.Models;

namespace PatternConversionTool.Services;

public interface IStilParser
{
    /// <summary>Parse a STIL file. When <paramref name="expandPattern"/> is false
    /// the (potentially huge) Pattern block is skipped so only the lightweight
    /// configuration — signals, signal groups and timing — is loaded. This keeps
    /// the UI responsive for large files; the full pattern is expanded later, at
    /// generation time.</summary>
    StilParseResult Parse(string filePath, bool expandPattern = true);
}

public class StilParseResult
{
    public List<Signal> Signals { get; set; } = new();
    public List<SignalGroup> SignalGroups { get; set; } = new();
    public TimingInfo Timing { get; set; } = new();
    public PatternInfo Pattern { get; set; } = new();

    // Kept for generators that need the original STIL ordering
    public List<string> AllInputSignals { get; set; } = new();
    public List<string> AllOutputSignals { get; set; } = new();
    public List<string> AllBidirSignals { get; set; } = new();
}
