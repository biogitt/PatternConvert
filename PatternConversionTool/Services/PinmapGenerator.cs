using System.Text;
using System.Xml;
using PatternConversionTool.Models;

namespace PatternConversionTool.Services;

/// <summary>Generates the .pinmap XML file.</summary>
public class PinmapGenerator
{
    public void Generate(string outputPath, IEnumerable<Signal> enabledSignals)
    {
        var settings = new XmlWriterSettings
        {
            Indent = true,
            IndentChars = "\t",
            Encoding = Encoding.UTF8
        };

        using var w = XmlWriter.Create(outputPath, settings);
        w.WriteStartDocument();
        w.WriteStartElement("PinMap",
            "http://www.ni.com/TestStand/SemiconductorModule/PinMap.xsd");
        w.WriteAttributeString("xmlns", "xsi", null,
            "http://www.w3.org/2001/XMLSchema-instance");
        w.WriteAttributeString("schemaVersion", "1.0");

        // Instruments
        w.WriteStartElement("Instruments");
        for (int i = 1; i <= 3; i++)
        {
            w.WriteStartElement("NIDigitalPatternInstrument");
            w.WriteAttributeString("name", $"Digital Pattern{i}");
            w.WriteAttributeString("numberOfChannels", "32");
            w.WriteEndElement();
        }
        for (int i = 1; i <= 2; i++)
        {
            w.WriteStartElement("NIDCPowerInstrument");
            w.WriteAttributeString("name", $"PSU{i}");
            w.WriteAttributeString("numberOfChannels", "4");
            w.WriteEndElement();
        }
        w.WriteEndElement();

        // Pins
        w.WriteStartElement("Pins");
        foreach (var sig in enabledSignals)
        {
            w.WriteStartElement("DUTPin");
            w.WriteAttributeString("name", sig.Name);
            w.WriteEndElement();
        }
        w.WriteEndElement();

        w.WriteStartElement("PinGroups");
        w.WriteEndElement();

        w.WriteEndElement(); // PinMap
        w.WriteEndDocument();
    }
}
