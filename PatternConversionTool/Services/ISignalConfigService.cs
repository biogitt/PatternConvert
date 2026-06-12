using PatternConversionTool.Models;

namespace PatternConversionTool.Services;

public interface ISignalConfigService
{
    /// <summary>
    /// Imports a signal configuration. Both the tool's own (native) CSV layout
    /// and the VectorPort "000"/"SIG" layout are supported and auto-detected.
    /// </summary>
    List<Signal> ImportCsv(string filePath);

    /// <summary>
    /// Exports the signal configuration in the tool's own native CSV layout,
    /// mirroring the signal panel columns. The file can be re-imported via
    /// <see cref="ImportCsv"/>.
    /// </summary>
    void ExportCsv(string filePath, IEnumerable<Signal> signals);

    /// <summary>
    /// Apply a "000"/"SIG" mapping onto a set of parsed STIL signals.
    /// The mapping's Remove? flag decides which signals are kept.
    /// </summary>
    void ApplyMapping(IList<Signal> stilSignals, IEnumerable<Signal> mapping);
}
