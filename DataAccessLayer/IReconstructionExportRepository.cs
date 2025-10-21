using Utility.Exports;
using Utility.Rendering;
using Utility.Classes;
using Utility.Classes.Discretizer;

namespace DataAccessLayer;

public interface IReconstructionExportRepository
{
    DataExportResult ExportReconstructionData(ReconstructionExportRequest request);
}

public sealed record ReconstructionExportRequest(IDiscretization Discretization,
                                                  IReadOnlyList<ReconstructionFrame> Frames,
                                                  IReadOnlyList<ReconstructionResult> Results,
                                                  ReconstructionFrame RenderFrame,
                                                  PotentialDisplayMode DisplayMode,
                                                  string TargetDirectory,
                                                  ConductivityDistribution InitialDistribution);
