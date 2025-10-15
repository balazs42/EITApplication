using Utility.Exports;
using Utility.Rendering;

namespace ServiceLayer;

public interface IReconstructionExportService
{
    Task<DataExportResult> ExportAsync(string rootDirectory,
                                       PotentialDisplayMode displayMode,
                                       CancellationToken cancellationToken = default);
}
