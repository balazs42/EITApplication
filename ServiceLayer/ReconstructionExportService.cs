using DataAccessLayer;
using Utility.Classes;
using Utility.Exports;
using Utility.Rendering;
using Workspace = Utility.Classes.Application.Workspace;

namespace ServiceLayer;

public sealed class ReconstructionExportService : IReconstructionExportService
{
    private readonly IReconstructionExportRepository _repository;

    public ReconstructionExportService(IReconstructionExportRepository repository)
    {
        _repository = repository;
    }

    public Task<DataExportResult> ExportAsync(string rootDirectory,
                                              PotentialDisplayMode displayMode,
                                              CancellationToken cancellationToken = default)
    {
        var results = Workspace.GetReconstructionResults().ToList();
        if (results.Count == 0)
            return Task.FromResult(DataExportResult.CreateFailure("No Results", "There are no reconstruction results to export."));

        var discretization = Workspace.GetDiscretization();
        if (discretization == null)
            return Task.FromResult(DataExportResult.CreateFailure("No Mesh", "Unable to determine the discretization for rendering."));

        var frames = Workspace.GetReconstructionFrames().ToList();
        if (frames.Count == 0)
            frames = results.SelectMany(r => r.Frames).ToList();

        var renderFrame = frames.LastOrDefault();
        if (renderFrame == null)
            return Task.FromResult(DataExportResult.CreateFailure("No Frames", "No reconstruction frames are available for export."));

        string directory = Path.Combine(rootDirectory, $"reconstruction_export_{DateTime.Now:yyyyMMdd_HHmmss}");

        var request = new ReconstructionExportRequest(discretization,
                                                      frames,
                                                      results,
                                                      renderFrame,
                                                      displayMode,
                                                      directory);

        return Task.Run(() => _repository.ExportReconstructionData(request), cancellationToken);
    }
}
