using PatternConversionTool.Services;
using PatternConversionTool.Models;

// Convert a STIL file with a 000/SIG mapping and diff the generated
// .digipatsrc against the reference (standard) conversion.
//
// The reference pattern can be enormous (millions of lines), so only the
// leading portion is compared. Defaults to the tpc6241 "pr" example.
string exDir   = @"C:\Users\kaguo\Desktop\PatternConvert\Examples\2023102\tpc6241";
string stilFile = args.Length > 0 ? args[0] : Path.Combine(exDir, "tpafe5173_pr.stil");
string sigFile  = args.Length > 1 ? args[1] : Path.Combine(exDir, "000.csv");
string refDigipat = args.Length > 2
    ? args[2]
    : Path.Combine(exDir, @"pr\tpafe5173_pr.digipatsrc");

// How many leading reference lines to compare. The default approximates 10%
// of the reference file (~3.9M lines -> ~400k).
int compareLines = args.Length > 3 && int.TryParse(args[3], out var cl) ? cl : 400_000;

Console.WriteLine("=== Inputs ===");
Console.WriteLine($"STIL      : {stilFile}");
Console.WriteLine($"Mapping   : {sigFile}");
Console.WriteLine($"Reference : {refDigipat}");
Console.WriteLine($"Compare   : first {compareLines:N0} reference lines");

var sw = System.Diagnostics.Stopwatch.StartNew();

Console.WriteLine("\n=== Parse STIL ===");
// Cap vector expansion so a multi-million-vector pattern does not exhaust
// memory. A small safety margin over compareLines covers header/label lines.
var parser = new StilParser { MaxVectors = compareLines + 5_000 };
var result = parser.Parse(stilFile);
Console.WriteLine($"STIL signals : {result.Signals.Count}");
Console.WriteLine($"Timesets     : {result.Timing.TimeSets.Count}");
Console.WriteLine($"Vectors (cap): {result.Pattern.Vectors.Count:N0}  [{sw.Elapsed.TotalSeconds:F1}s]");

Console.WriteLine("\n=== Load 000 mapping ===");
var csvSvc = new SignalConfigService();
var mapping = csvSvc.ImportCsv(sigFile);
Console.WriteLine($"Mapping rows : {mapping.Count}");
Console.WriteLine($"Rows kept    : {mapping.Count(m => m.Enabled)} (Remove?=false)");

Console.WriteLine("\n=== Apply mapping to STIL signals ===");
var stilSignals = result.Signals.ToList();
csvSvc.ApplyMapping(stilSignals, mapping);
var enabled = stilSignals.Where(s => s.Enabled).OrderBy(s => s.MappingOrder).ToList();
Console.WriteLine($"Enabled pins : {enabled.Count}");
foreach (var s in enabled)
    Console.WriteLine($"  pin={s.Name}  stil={s.OriginalStilName}  dir={s.Direction}");

Console.WriteLine("\n=== Generate digipatsrc ===");
string outDir = Path.Combine(Path.GetTempPath(), "PatternConvertTest");
Directory.CreateDirectory(outDir);
string outDigipat = Path.Combine(outDir, "tpafe5173_pr.digipatsrc");
new DigiPatGenerator().Generate(outDigipat, result.Pattern, enabled, stilFile);
Console.WriteLine($"Wrote {outDigipat}  [{sw.Elapsed.TotalSeconds:F1}s]");

// Also generate via the streaming path and confirm it is byte-identical to the
// in-memory path. Streaming keeps memory flat for huge patterns.
Console.WriteLine("\n=== Generate digipatsrc (streaming) ===");
string outStream = Path.Combine(outDir, "tpafe5173_pr.stream.digipatsrc");
var streamParser = new StilParser { MaxVectors = compareLines + 5_000 };
new DigiPatGenerator().GenerateStreaming(outStream, result.Pattern, enabled,
    sink => streamParser.ParseStreaming(stilFile, sink).Pattern.IsComplete, stilFile);
bool identical = File.ReadAllText(outDigipat) == File.ReadAllText(outStream);
Console.WriteLine($"Streaming output identical to in-memory: {identical}");
if (!identical)
{
    var a = File.ReadAllLines(outDigipat);
    var b = File.ReadAllLines(outStream);
    int max = Math.Max(a.Length, b.Length);
    for (int i = 0, shown = 0; i < max && shown < 10; i++)
    {
        string la = i < a.Length ? a[i] : "<EOF>";
        string lb = i < b.Length ? b[i] : "<EOF>";
        if (la != lb) { Console.WriteLine($"  L{i + 1}\n    mem: {la}\n    str: {lb}"); shown++; }
    }
}

Console.WriteLine("\n=== Diff (leading lines) ===");
DiffLeadingLines(refDigipat, outDigipat, compareLines);

Console.WriteLine($"\nDone in {sw.Elapsed.TotalSeconds:F1}s");

// ---------------------------------------------------------------------------
// Streams both files line-by-line and reports the first mismatches plus an
// overall match ratio over the compared window. The tool banners differ in
// length (VectorPort vs PatternConversionTool), so both readers are first
// advanced to the shared "pattern ..." declaration to align the vector bodies.
static void DiffLeadingLines(string referencePath, string generatedPath, int maxLines)
{
    using var refReader = new StreamReader(referencePath);
    using var genReader = new StreamReader(generatedPath);

    string? refSync = AdvanceTo(refReader, l => l.TrimStart().StartsWith("pattern "));
    string? genSync = AdvanceTo(genReader, l => l.TrimStart().StartsWith("pattern "));
    if (refSync == null || genSync == null)
    {
        Console.WriteLine("  Could not locate 'pattern' declaration in one of the files.");
        return;
    }
    Console.WriteLine("  Aligned at pattern declaration:");
    Console.WriteLine($"    ref: {Trunc(refSync)}");
    Console.WriteLine($"    gen: {Trunc(genSync)}");
    Console.WriteLine(Normalize(refSync) == Normalize(genSync)
        ? "    -> declaration MATCHES\n"
        : "    -> declaration DIFFERS\n");

    int lineNo = 0, compared = 0, matches = 0, shownDiffs = 0;
    const int maxShownDiffs = 40;
    int firstDiffLine = -1;

    while (lineNo < maxLines)
    {
        string? r = refReader.ReadLine();
        string? g = genReader.ReadLine();
        if (r == null && g == null) break;
        lineNo++;

        string rn = Normalize(r);
        string gn = Normalize(g);

        compared++;
        if (rn == gn)
        {
            matches++;
        }
        else
        {
            if (firstDiffLine < 0) firstDiffLine = lineNo;
            if (shownDiffs < maxShownDiffs)
            {
                Console.WriteLine($"  body L{lineNo} DIFF");
                Console.WriteLine($"    ref: {Trunc(r)}");
                Console.WriteLine($"    gen: {Trunc(g)}");
                shownDiffs++;
            }
        }
    }

    Console.WriteLine($"\nBody lines compared : {compared:N0}");
    Console.WriteLine($"Matching            : {matches:N0}");
    double pct = compared == 0 ? 100.0 : 100.0 * matches / compared;
    Console.WriteLine($"Match ratio         : {pct:F2}%");
    Console.WriteLine(firstDiffLine < 0
        ? "Result              : IDENTICAL over compared window"
        : $"First body diff     : line {firstDiffLine}");
}

// Reads lines until one satisfies the predicate; returns it (or null at EOF).
static string? AdvanceTo(StreamReader reader, Func<string, bool> predicate)
{
    string? line;
    while ((line = reader.ReadLine()) != null)
        if (predicate(line))
            return line;
    return null;
}

static string Normalize(string? s) =>
    s == null ? "" : System.Text.RegularExpressions.Regex.Replace(s.Trim(), @"\s+", " ");

static string Trunc(string? s) =>
    s == null ? "<EOF>" : (s.Length > 100 ? s[..100] + "..." : s);
