using PatternConversionTool.Services;
using PatternConversionTool.Models;

string exDir = @"C:\Users\kaguo\Desktop\PatternConvert\Examples\20241128\tpc2884_atpg_iddq_patterns";
string stilFile = Path.Combine(exDir, "tpc2884_chip_top_wrap_pr.stil");
string sigFile = Path.Combine(exDir, "000");
string refDir = Path.Combine(exDir, @"Converted2884\pr");

Console.WriteLine("=== Parse STIL ===");
var parser = new StilParser();
var result = parser.Parse(stilFile);
Console.WriteLine($"Signals: {result.Signals.Count}");
Console.WriteLine($"TimeSets: {result.Timing.TimeSets.Count}");
Console.WriteLine($"Vectors: {result.Pattern.Vectors.Count}");

Console.WriteLine("\n=== Load 000 mapping ===");
var csvSvc = new SignalConfigService();
var mappings = csvSvc.ImportCsv(sigFile);
Console.WriteLine($"Mapped: {mappings.Count}");
foreach (var m in mappings)
    Console.WriteLine($"  {m.OriginalStilName} -> {m.Name} (Enabled={m.Enabled} Dir={m.Direction})");

// Apply mapping: match by OriginalStilName, update Name/Enabled
foreach (var sig in result.Signals)
{
    var map = mappings.FirstOrDefault(m => m.OriginalStilName == sig.OriginalStilName);
    if (map != null)
    {
        sig.Name = map.Name;
        sig.Enabled = map.Enabled;
    }
}

var enabled = result.Signals.Where(s => s.Enabled).ToList();
Console.WriteLine($"\nEnabled signals: {enabled.Count}");
foreach (var s in enabled)
    Console.WriteLine($"  {s.Name} ({s.Direction}) STIL={s.OriginalStilName}");

// Generate
string outDir = Path.Combine(Path.GetTempPath(), "PatternConvertTest");
Directory.CreateDirectory(outDir);

Console.WriteLine("\n=== Generating pinmap ===");
new PinmapGenerator().Generate(Path.Combine(outDir, "test.pinmap"), enabled);
Console.WriteLine(File.ReadAllText(Path.Combine(outDir, "test.pinmap")));

Console.WriteLine("\n=== Generating digitiming ===");
new TimingGenerator(result.SignalGroups).Generate(
    Path.Combine(outDir, "test.digitiming"), result.Timing, enabled);
var timLines = File.ReadAllLines(Path.Combine(outDir, "test.digitiming"));
Console.WriteLine($"Lines: {timLines.Length}");
foreach (var l in timLines.Take(30))
    Console.WriteLine(l);

Console.WriteLine("\n=== Generating digipatsrc ===");
new DigiPatGenerator().Generate(
    Path.Combine(outDir, "test.digipatsrc"), result.Pattern, enabled, stilFile);
var patLines = File.ReadAllLines(Path.Combine(outDir, "test.digipatsrc"));
Console.WriteLine($"Lines: {patLines.Length}");
foreach (var l in patLines.Take(30))
    Console.WriteLine(l);

// Compare first 25 pattern lines with reference
Console.WriteLine("\n=== Compare with reference ===");
var refLines = File.ReadAllLines(Path.Combine(refDir, "tpc2884_chip_top_wrap_pr.digipatsrc"));
Console.WriteLine($"Reference lines: {refLines.Length}");
Console.WriteLine($"Generated lines: {patLines.Length}");
int diffs = 0;
for (int i = 0; i < Math.Min(50, Math.Min(refLines.Length, patLines.Length)); i++)
{
    if (refLines[i].Trim() != patLines[i].Trim())
    {
        Console.WriteLine($"DIFF line {i}:");
        Console.WriteLine($"  REF: [{refLines[i].Trim()}]");
        Console.WriteLine($"  GOT: [{patLines[i].Trim()}]");
        diffs++;
    }
}
Console.WriteLine($"\nDiffs in first 50 lines: {diffs}");
