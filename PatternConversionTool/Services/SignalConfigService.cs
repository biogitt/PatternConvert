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
                Name = p[1],            // Group/Alias Name
                Direction = direction,
                Enabled = !remove,
                Remote = remove,
                Group = isScan ? "Scan" : "",
                Source = "CSV",
                OriginalStilName = p[11] // Original Signal Name
            });
        }
        return result;
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
