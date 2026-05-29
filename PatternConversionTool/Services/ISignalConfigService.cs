using PatternConversionTool.Models;

namespace PatternConversionTool.Services;

public interface ISignalConfigService
{
    List<Signal> ImportCsv(string filePath);
    void ExportCsv(string filePath, IEnumerable<Signal> signals);
}
