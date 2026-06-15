using System.Diagnostics;
using System.IO;

namespace PatternConversionTool.Services;

/// <summary>
/// Thin wrapper around the NI Digital Pattern Compiler command-line tool
/// (<c>DigitalPatternCompiler.exe</c>). After the tool generates the text
/// pattern (<c>.digipatsrc</c>) it can be compiled to the NI binary
/// (<c>.digipat</c>) format, but only when the compiler is installed on the
/// machine. Test <see cref="IsAvailable"/> before calling <see cref="Compile"/>.
/// </summary>
public class DigitalPatternCompiler
{
    /// <summary>Full path to the compiler executable, or <c>null</c> when it was
    /// not found on this machine.</summary>
    public string? CompilerPath { get; }

    public DigitalPatternCompiler(string? compilerPath = null)
    {
        CompilerPath = !string.IsNullOrEmpty(compilerPath) && File.Exists(compilerPath)
            ? compilerPath
            : DefaultCompilerPaths().FirstOrDefault(File.Exists);
    }

    /// <summary>Whether the Digital Pattern Compiler is installed and usable.</summary>
    public bool IsAvailable => CompilerPath != null;

    /// <summary>
    /// Compile a text pattern file (<paramref name="patternSrcPath"/>) into the
    /// NI binary (<c>.digipat</c>) format using the supplied pin map. The
    /// <c>-pinmap</c> option is required when compiling a text pattern file. The
    /// compiled file is written next to the source using the compiler's default
    /// naming.
    /// </summary>
    public CompileResult Compile(string patternSrcPath, string pinmapPath)
    {
        if (!IsAvailable)
            throw new InvalidOperationException(
                "The NI Digital Pattern Compiler is not installed on this machine.");

        var psi = new ProcessStartInfo
        {
            FileName = CompilerPath!,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = Path.GetDirectoryName(patternSrcPath) ?? Environment.CurrentDirectory
        };
        // -pinmap is required to compile a text pattern file; the source path
        // follows the options.
        psi.ArgumentList.Add("-pinmap");
        psi.ArgumentList.Add(pinmapPath);
        psi.ArgumentList.Add(patternSrcPath);

        using var proc = Process.Start(psi)
            ?? throw new InvalidOperationException("Failed to start the Digital Pattern Compiler.");

        string stdout = proc.StandardOutput.ReadToEnd();
        string stderr = proc.StandardError.ReadToEnd();
        proc.WaitForExit();

        return new CompileResult(proc.ExitCode, stdout, stderr);
    }

    /// <summary>Candidate install locations of the compiler executable.</summary>
    private static IEnumerable<string> DefaultCompilerPaths()
    {
        const string relative =
            @"National Instruments\Digital Pattern Compiler\DigitalPatternCompiler.exe";

        foreach (var programFiles in new[]
                 {
                     Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                     Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86)
                 })
        {
            if (!string.IsNullOrEmpty(programFiles))
                yield return Path.Combine(programFiles, relative);
        }
    }
}

/// <summary>Outcome of a <see cref="DigitalPatternCompiler.Compile"/> call.</summary>
public record CompileResult(int ExitCode, string StandardOutput, string StandardError)
{
    /// <summary>The compiler reports success with a zero exit code.</summary>
    public bool Success => ExitCode == 0;
}
