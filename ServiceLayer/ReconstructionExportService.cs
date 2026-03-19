using DataAccessLayer;
using System;
using System.Collections.Generic;
using System.Linq;
using Utility.Classes;
using Utility.Classes.Discretizer;
using Utility.Classes.Discretizer.FiniteElementMesh;
using Utility.Classes.Discretizer.LatticeBoltzmannGrid;
using Utility.Classes.Measurement;
using Utility.Classes.Reconstruction.VirtualElectrodes;
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
                                              PotentialDisplayMode potentialDisplayMode,
                                              ConductivityDisplayMode conductivityDisplayMode,
                                              CancellationToken cancellationToken = default)
    {
        var results = Workspace.GetReconstructionResults().ToList();
        if (results.Count == 0)
            return Task.FromResult(DataExportResult.CreateFailure("No Results", "There are no reconstruction results to export."));

        var discretization = results.Select(r => r.Discretization)
                                    .FirstOrDefault(d => d != null)
                            ?? Workspace.GetDiscretization();

        if (discretization == null)
            return Task.FromResult(DataExportResult.CreateFailure("No Mesh", "Unable to determine the discretization for rendering."));

        var frames = Workspace.GetReconstructionFrames().ToList();
        if (frames.Count == 0)
            frames = results.SelectMany(r => r.Frames).ToList();

        var renderFrame = frames.LastOrDefault();
        if (renderFrame == null)
            return Task.FromResult(DataExportResult.CreateFailure("No Frames", "No reconstruction frames are available for export."));

        string directory = Path.Combine(rootDirectory, $"reconstruction_export_{DateTime.Now:yyyyMMdd_HHmmss}");

        var initialDistribution = Workspace.GetInitialConductivityDistribution()
                                   ?? results[0].InitialConductivitiyDistribution;

        var measurementPattern = Workspace.GetMeasurementPattern();
        var configuration = CreateConfigurationSnapshot(discretization,
                                                        initialDistribution,
                                                        measurementPattern);

        var request = new ReconstructionExportRequest(discretization,
                                                      frames,
                                                      results,
                                                      renderFrame,
                                                      potentialDisplayMode,
                                                      conductivityDisplayMode,
                                                      directory,
                                                      initialDistribution,
                                                      measurementPattern,
                                                      configuration);

        return Task.Run(() => _repository.ExportReconstructionData(request), cancellationToken);
    }

    private static ReconstructionConfigurationSnapshot CreateConfigurationSnapshot(IDiscretization discretization,
                                                                                   ConductivityDistribution initialDistribution,
                                                                                   MeasurementPattern? measurementPattern)
    {
        var parameters = Workspace.GetReconstructionParameters();
        var measurementSource = Workspace.GetMeasurementSource();
        var measurementSetup = Workspace.GetElectrodeMeasurementSetup();
        var measurementLabel = Workspace.GetImportedMeasurementLabel();

        var channelSnapshots = measurementPattern?.Channels
            .Select(c => new MeasurementChannelSnapshot(c.TargetIndex, c.FirstElectrodeIndex, c.SecondElectrodeIndex))
            .ToList() ?? new List<MeasurementChannelSnapshot>();

        var measurementSnapshot = new MeasurementConfigurationSnapshot(
            measurementSource.ToString(),
            measurementSetup.ToString(),
            measurementLabel,
            parameters.UsePotentialDifferences,
            measurementPattern is not null,
            measurementPattern?.Representation.ToString(),
            measurementPattern?.SanitizedLength,
            channelSnapshots.Count,
            channelSnapshots);

        var reconstructionSection = new ReconstructionParameterSection(
            parameters.DifferentialEquationSolver.ToString(),
            parameters.RegularizationTechnique.ToString(),
            parameters.ErrorMetric.ToString(),
            parameters.NumericSolver.ToString(),
            parameters.NumericOptimizer.ToString(),
            parameters.DrivePattern.ToString(),
            parameters.DrivePatternSkip,
            parameters.UsePotentialDifferences,
            parameters.UseOmpParallelization,
            parameters.UseCudaAcceleration,
            parameters.MeasurementNoiseType.ToString(),
            parameters.MeasurementNoiseAmplitude);

        var workspaceSection = new WorkspaceParameterSection(
            Workspace.MaxIterationCount,
            Workspace.StepSize,
            Workspace.RegularizationWeight,
            Workspace.ConductivityMinimumBound,
            Workspace.ConductivityMaximumBound);

        var meshSnapshot = CreateMeshSnapshot(discretization);
        var initialSnapshot = CreateInitialDistributionSnapshot(parameters.InitialDistributionType.ToString(), initialDistribution);
        var virtualElectrodeSnapshot = CreateVirtualElectrodeSnapshot(parameters.VirtualElectrodeSettings);

        return new ReconstructionConfigurationSnapshot(
            DateTime.UtcNow,
            reconstructionSection,
            workspaceSection,
            measurementSnapshot,
            meshSnapshot,
            initialSnapshot,
            virtualElectrodeSnapshot);
    }

    private static VirtualElectrodeConfigurationSnapshot CreateVirtualElectrodeSnapshot(VirtualElectrodeSettings settings)
        => new(
            settings.UseVirtualElectrodes,
            settings.Method,
            settings.VirtualElectrodesPerGap,
            settings.LinearCombinationAlpha,
            settings.HarrachLambda,
            settings.NdMaxMode);

    private static MeshConfigurationSnapshot CreateMeshSnapshot(IDiscretization discretization)
    {
        var metadata = discretization.Metadata ?? new DiscretizationMetaData();
        var parameterCopy = metadata.Parameters != null
            ? new Dictionary<string, string>(metadata.Parameters)
            : new Dictionary<string, string>();

        string meshType = discretization switch
        {
            FEMMesh => "FEM",
            LBMGrid => "LBM",
            _ => discretization.GetType().Name
        };

        string? sourceFile = InferMeshSourceFile(parameterCopy);

        int elementCount = metadata.ElementCount > 0
            ? metadata.ElementCount
            : discretization.GetElements().Count;

        int electrodeCount = discretization.GetElectrodes().Count;

        return new MeshConfigurationSnapshot(
            meshType,
            metadata.Generator ?? string.Empty,
            elementCount,
            electrodeCount,
            metadata.CreatedOn,
            sourceFile,
            parameterCopy);
    }

    private static string? InferMeshSourceFile(IReadOnlyDictionary<string, string> parameters)
    {
        foreach (var kvp in parameters)
        {
            if (kvp.Key.Contains("file", StringComparison.OrdinalIgnoreCase) ||
                kvp.Key.Contains("path", StringComparison.OrdinalIgnoreCase))
            {
                return kvp.Value;
            }
        }

        return null;
    }

    private static InitialDistributionSnapshot CreateInitialDistributionSnapshot(string initialDistributionType,
                                                                                ConductivityDistribution? distribution)
    {
        if (distribution?.Conductivities == null || distribution.Conductivities.Count == 0)
        {
            return new InitialDistributionSnapshot(initialDistributionType,
                                                   false,
                                                   distribution?.Conductivities.Count ?? 0,
                                                   double.NaN,
                                                   double.NaN,
                                                   double.NaN,
                                                   double.NaN);
        }

        var validValues = distribution.Conductivities.Values
            .Where(v => !double.IsNaN(v) && !double.IsInfinity(v))
            .ToList();

        if (validValues.Count == 0)
        {
            return new InitialDistributionSnapshot(initialDistributionType,
                                                   false,
                                                   distribution.Conductivities.Count,
                                                   double.NaN,
                                                   double.NaN,
                                                   double.NaN,
                                                   double.NaN);
        }

        double min = validValues.Min();
        double max = validValues.Max();
        double mean = validValues.Average();
        double variance = validValues.Sum(v => (v - mean) * (v - mean)) / validValues.Count;
        double stdDev = Math.Sqrt(variance);

        return new InitialDistributionSnapshot(initialDistributionType,
                                               true,
                                               distribution.Conductivities.Count,
                                               min,
                                               max,
                                               mean,
                                               stdDev);
    }
}
