using System.Text;
using System.Xml;
using PatternConversionTool.Models;

namespace PatternConversionTool.Services;

/// <summary>Generates the .digitiming XML file.</summary>
public class TimingGenerator
{
    private readonly List<SignalGroup> _groups;

    public TimingGenerator(List<SignalGroup> stilGroups)
    {
        _groups = stilGroups;
    }

    public void Generate(string outputPath, TimingInfo timing,
        IReadOnlyList<Signal> enabledSignals)
    {
        var settings = new XmlWriterSettings
        {
            Indent = true,
            IndentChars = "\t",
            Encoding = Encoding.UTF8
        };

        // map from STIL name ? Signal (for rename & direction)
        var stilMap = enabledSignals.ToDictionary(s => s.OriginalStilName, s => s);

        using var w = XmlWriter.Create(outputPath, settings);
        w.WriteStartDocument();
        w.WriteStartElement("TimingFile",
            "http://www.ni.com/Semiconductor/Timing");
        w.WriteAttributeString("xmlns", "xsd", null, "http://www.w3.org/2001/XMLSchema");
        w.WriteAttributeString("xmlns", "xsi", null,
            "http://www.w3.org/2001/XMLSchema-instance");
        w.WriteAttributeString("schemaVersion", "1.0");

        w.WriteStartElement("TimingSheet");
        w.WriteStartElement("TimeSets");

        foreach (var ts in timing.TimeSets)
        {
            w.WriteStartElement("TimeSet");
            w.WriteAttributeString("name", ts.Name);
            w.WriteElementString("Period", Eng(ts.PeriodSeconds));

            w.WriteStartElement("PinEdges");
            foreach (var sig in enabledSignals)
            {
                var wfs = GetWaveforms(ts, sig.OriginalStilName);
                WritePinEdge(w, sig.Name, sig.Direction, wfs, ts.PeriodSeconds);
            }
            w.WriteEndElement(); // PinEdges
            w.WriteEndElement(); // TimeSet
        }

        w.WriteEndElement(); // TimeSets
        w.WriteEndElement(); // TimingSheet
        w.WriteEndElement(); // TimingFile
        w.WriteEndDocument();
    }

    private List<WaveformDef> GetWaveforms(TimeSetDef ts, string stilName)
    {
        var result = new List<WaveformDef>();
        foreach (var wf in ts.Waveforms)
        {
            if (wf.SignalOrGroup == stilName)
            { result.Add(wf); continue; }

            // check groups
            var grp = _groups.FirstOrDefault(g => g.Name == wf.SignalOrGroup);
            if (grp != null && grp.SignalNames.Contains(stilName))
                result.Add(wf);
        }
        return result;
    }

    private static void WritePinEdge(XmlWriter w, string pinName, string direction,
        List<WaveformDef> wfs, double period)
    {
        w.WriteStartElement("PinEdge");
        w.WriteAttributeString("pin", pinName);

        var pulse = wfs.FirstOrDefault(f => f.WFC == 'P');
        bool isOut = direction == "Out";

        if (pulse != null && pulse.Edges.Count >= 3)
        {
            var e = pulse.Edges;
            string tag = e[0].Action == 'D' ? "ReturnToLow" : "ReturnToHigh";
            w.WriteStartElement(tag);
            w.WriteElementString("On",     Eng(e[0].TimeSeconds));
            w.WriteElementString("Data",   Eng(e[1].TimeSeconds));
            w.WriteElementString("Return", Eng(e[2].TimeSeconds));
            w.WriteElementString("Off",    Eng(e[2].TimeSeconds));
            w.WriteEndElement();
        }
        else if (isOut)
        {
            w.WriteStartElement("DriveNonReturn");
            w.WriteElementString("On", "0");
            w.WriteElementString("Data", "0");
            w.WriteElementString("Off", "0");
            w.WriteEndElement();
        }
        else
        {
            w.WriteStartElement("DriveNonReturn");
            w.WriteElementString("On", "0");
            w.WriteElementString("Data", "0");
            w.WriteElementString("Off", Eng(period));
            w.WriteEndElement();
        }

        // CompareStrobe
        double strobe = 0;
        var cmp = wfs.FirstOrDefault(f => f.WFC is 'H' or 'L' or 'T')
               ?? (isOut ? wfs.FirstOrDefault(f => f.WFC == 'X') : null);
        if (cmp != null && cmp.Edges.Count >= 2)
            strobe = cmp.Edges[1].TimeSeconds;

        w.WriteStartElement("CompareStrobe");
        w.WriteElementString("Strobe", strobe > 0 ? Eng(strobe) : "0");
        w.WriteEndElement();

        w.WriteElementString("DataSource", "Pattern");
        w.WriteEndElement(); // PinEdge
    }

    private static string Eng(double v)
    {
        if (v == 0) return "0";
        double a = Math.Abs(v);
        if (a >= 1e-3  && a < 1)   return $"{Math.Round(v * 1e3,  10)}E-3";
        if (a >= 1e-6  && a < 1e-3) return $"{Math.Round(v * 1e6,  10)}E-6";
        if (a >= 1e-9  && a < 1e-6) return $"{Math.Round(v * 1e9,  10)}E-9";
        if (a >= 1e-12 && a < 1e-9) return $"{Math.Round(v * 1e12, 10)}E-12";
        return v.ToString("G");
    }
}
