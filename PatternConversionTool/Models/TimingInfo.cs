namespace PatternConversionTool.Models;

/// <summary>
/// Represents one WaveformTable from the STIL Timing block.
/// </summary>
public class TimingInfo
{
    public List<TimeSetDef> TimeSets { get; set; } = new();
}

public class TimeSetDef
{
    public string Name { get; set; } = "";
    public double PeriodSeconds { get; set; }
    public List<WaveformDef> Waveforms { get; set; } = new();
}

/// <summary>
/// One waveform entry, e.g. "all_inputs" { 0 { '0ns' D; } }
/// </summary>
public class WaveformDef
{
    public string SignalOrGroup { get; set; } = "";
    public char WFC { get; set; }
    public List<EdgeDef> Edges { get; set; } = new();
}

public class EdgeDef
{
    public double TimeSeconds { get; set; }
    public char Action { get; set; }  // D, U, Z, X, H, L, T, N
}
