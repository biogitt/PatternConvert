using System.IO;
using PatternConversionTool.Models;

namespace PatternConversionTool.Services;

/// <summary>
/// Import / export signal configuration in the "000" / "SIG" CSV format
/// used by VectorPort.
/// </summary>
public class SignalConfigService : ISignalConfigService
{
    public List<Signal> ImportCsv(string filePath)
    {
        var result = new List<Signal>();
        foreach (var line in File.ReadAllLines(filePath))
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
                Remote = remove,
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
                sig.Remote = map.Remote;
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

    public void ExportCsv(string filePath, IEnumerable<Signal> signals)
    {
        using var w = new StreamWriter(filePath);
        w.WriteLine("# PatternConversionTool");
        w.WriteLine($"# File saved on {DateTime.Now:MMMM d, yyyy} at {DateTime.Now:hh:mm:ss tt}");
        w.WriteLine("# Order, Group/Alias Name, Signal Name, Bus Index, Direction, Radix, " +
                    "Is Static?, Static Value, Remove?, Is Scan?, Original Group Name, " +
                    "Original Signal Name (without bus index values), Force Timing Mode?, " +
                    "Remove Differential Pin?, Map Signal to addition HSVG Columns, ReMap State?, ReMap State Value");

        int order = 0;
        foreach (var sig in signals)
        {
            string dir = sig.Direction switch
            {
                "In" => "Input",
                "Out" => "Output",
                "InOut" => "Bidir",
                _ => sig.Direction
            };
            bool isScan = sig.Group == "Scan";
            string origStil = string.IsNullOrEmpty(sig.OriginalStilName) ? sig.Name : sig.OriginalStilName;

            w.WriteLine($"{order},{sig.Name},{sig.Name},,{dir},Binary,false,,{(!sig.Enabled).ToString().ToLower()},{isScan.ToString().ToLower()},{sig.Name},{origStil},false,false,,false,");
            order++;
        }
    }
}
