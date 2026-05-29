using PatternConversionTool.Models;

namespace PatternConversionTool.Services;

public interface IStilParser
{
    StilParseResult Parse(string filePath);
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
