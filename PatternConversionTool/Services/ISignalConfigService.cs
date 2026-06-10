using PatternConversionTool.Models;

namespace PatternConversionTool.Services;

public interface ISignalConfigService
{
    List<Signal> ImportCsv(string filePath);
    void ExportCsv(string filePath, IEnumerable<Signal> signals);

    /// <summary>
    /// Apply a "000"/"SIG" mapping onto a set of parsed STIL signals.
    /// The mapping's Remove? flag decides which signals are kept.
    /// </summary>
    void ApplyMapping(IList<Signal> stilSignals, IEnumerable<Signal> mapping);
}
