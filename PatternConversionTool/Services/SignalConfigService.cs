using System.IO;
using PatternConversionTool.Models;

namespace PatternConversionTool.Services;

/// <summary>
/// Import / export signal configuration.
/// <para>Two CSV layouts are supported on import and auto-detected:</para>
/// <list type="bullet">
///   <item>The tool's own native layout: a simple header row mirroring the
///   signal panel (Name, Direction, Enabled, Group, Source, STILName).</item>
///   <item>The "000" / "SIG" layout produced by VectorPort.</item>
/// </list>
/// Export always writes the native layout.
/// </summary>
public class SignalConfigService : ISignalConfigService
{
    public List<Signal> ImportCsv(string filePath)
    {
        var lines = File.ReadAllLines(filePath);
        return IsNativeFormat(lines)
            ? ImportNativeCsv(lines)
            : ImportVectorPortCsv(lines);
    }

    /// <summary>
    /// Detects the tool's own (native) CSV by its header row, whose first
    /// column is "Name". VectorPort "000"/"SIG" data rows start with a numeric
    /// order column, so this never misfires for that format.
    /// </summary>
    private static bool IsNativeFormat(IEnumerable<string> lines)
    {
        foreach (var line in lines)
        {
            if (line.StartsWith('#') || string.IsNullOrWhiteSpace(line))
                continue;

            var fields = line.Split(',');
            return fields.Length > 0 &&
                   fields[0].Trim().Equals("Name", StringComparison.OrdinalIgnoreCase);
        }
        return false;
    }

    /// <summary>
    /// Reads the tool's own CSV layout. Columns are resolved by header name so
    /// their order is not significant.
    /// </summary>
    private static List<Signal> ImportNativeCsv(string[] lines)
    {
        var result = new List<Signal>();
        string[]? header = null;

        foreach (var line in lines)
        {
            if (line.StartsWith('#') || string.IsNullOrWhiteSpace(line))
                continue;

            var p = line.Split(',');
            if (header == null)
            {
                // First non-comment line is the column header.
                header = Array.ConvertAll(p, h => h.Trim());
                continue;
            }

            string Field(string column)
            {
                int idx = Array.FindIndex(header,
                    h => h.Equals(column, StringComparison.OrdinalIgnoreCase));
                return idx >= 0 && idx < p.Length ? p[idx].Trim() : "";
            }

            string name = Field("Name");
            if (string.IsNullOrEmpty(name)) continue;

            // Default to enabled when the column is missing or unparseable.
            bool enabled = !bool.TryParse(Field("Enabled"), out var en) || en;
            string stil = Field("STILName");

            result.Add(new Signal
            {
                Name = name,
                Direction = Field("Direction"),
                Enabled = enabled,
                Group = Field("Group"),
                Source = "CSV",
                OriginalStilName = string.IsNullOrEmpty(stil) ? name : stil
            });
        }
        return result;
    }

    /// <summary>
    /// Reads the VectorPort "000" / "SIG" CSV layout.
    /// </summary>
    private static List<Signal> ImportVectorPortCsv(string[] lines)
    {
        var result = new List<Signal>();
        foreach (var line in lines)
        {
            if (line.StartsWith('#') || string.IsNullOrWhiteSpace(line))
                continue;

            var p = line.Split(',');
            if (p.Length < 12) continue;

            // Column layout of the "000" / "SIG" file:
            //  0 Order, 1 Group/Alias Name, 2 Signal Name, 3 Bus Index,
            //  4 Direction, 5 Radix, 6 Is Static?, 7 Static Value,
            //  8 Remove?, 9 Is Scan?, 10 Original Group Name,
            // 11 Original Signal Name (without bus index values)
            //
            // Rule: Remove? == true  -> signal is excluded from conversion
            //       Remove? == false -> signal is kept / converted
            bool remove = p.Length > 8 && bool.TryParse(p[8], out var r) && r;
            bool isScan = p.Length > 9 && bool.TryParse(p[9], out var s) && s;

            string direction = p[4] switch
            {
                "Input" => "In",
                "Output" => "Out",
                "Bidir" => "InOut",
                _ => p[4]
            };

            result.Add(new Signal
            {
                Name = p[1],            // Group/Alias Name (becomes the DUT pin name)
                Direction = direction,
                Enabled = !remove,      // Remove? == false -> keep
                Group = isScan ? "Scan" : "",
                Source = "CSV",
                // Reconstruct the exact STIL signal name from the original name
                // plus the optional bus index, e.g. "AVDD_PGA" + "7" -> "AVDD_PGA[7]".
                OriginalStilName = BuildStilName(p[11], p[3])
            });
        }
        return result;
    }

    /// <summary>
    /// Apply a "000"/"SIG" mapping onto an existing set of STIL signals.
    /// The mapping's <c>Remove?</c> flag decides which signals are kept
    /// (Enabled) and what alias/pin name they get. Signals present in the
    /// STIL file but missing from the mapping are excluded (Enabled = false).
    /// </summary>
    public void ApplyMapping(IList<Signal> stilSignals, IEnumerable<Signal> mapping)
    {
        // Index mapping rows by their reconstructed STIL name for fast lookup,
        // remembering the order in which they appear in the 000/SIG file.
        var byStilName = new Dictionary<string, Signal>(StringComparer.Ordinal);
        var order = new Dictionary<string, int>(StringComparer.Ordinal);
        int idx = 0;
        foreach (var m in mapping)
        {
            byStilName[m.OriginalStilName] = m;
            order[m.OriginalStilName] = idx++;
        }

        foreach (var sig in stilSignals)
        {
            if (byStilName.TryGetValue(sig.OriginalStilName, out var map))
            {
                // Keep / rename according to the mapping.
                sig.Name = map.Name;
                sig.Enabled = map.Enabled;   // driven by Remove? == false
                sig.Group = map.Group;
                sig.MappingOrder = order[sig.OriginalStilName];
            }
            else
            {
                // Not referenced by the mapping file -> exclude from conversion.
                sig.Enabled = false;
                sig.MappingOrder = int.MaxValue;
            }
        }
    }

    /// <summary>
    /// Combine an original signal name with a bus index to reproduce the STIL
    /// signal name. Returns the bare name when no bus index is present.
    /// </summary>
    private static string BuildStilName(string originalName, string busIndex)
    {
        originalName = originalName.Trim();
        busIndex = busIndex.Trim();
        return string.IsNullOrEmpty(busIndex)
            ? originalName
            : $"{originalName}[{busIndex}]";
    }

    /// <summary>
    /// Writes the tool's own native CSV layout, mirroring the columns of the
    /// signal panel. This file can be re-imported via <see cref="ImportCsv"/>.
    /// </summary>
    public void ExportCsv(string filePath, IEnumerable<Signal> signals)
    {
        using var w = new StreamWriter(filePath);
        w.WriteLine("# PatternConversionTool signal configuration");
        w.WriteLine($"# File saved on {DateTime.Now:MMMM d, yyyy} at {DateTime.Now:hh:mm:ss tt}");
        w.WriteLine("Name,Direction,Enabled,Group,Source,STILName");

        foreach (var sig in signals)
        {
            string stil = string.IsNullOrEmpty(sig.OriginalStilName)
                ? sig.Name : sig.OriginalStilName;
            w.WriteLine($"{sig.Name},{sig.Direction}," +
                        $"{sig.Enabled.ToString().ToLowerInvariant()}," +
                        $"{sig.Group},{sig.Source},{stil}");
        }
    }
}
