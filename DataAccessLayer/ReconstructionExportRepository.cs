using SkiaSharp;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Json;
using Utility.Classes;
using Utility.Classes.Discretizer;
using Utility.Classes.Measurement;
using Utility.Classes.Reconstruction.Metrics;
using Utility.Classes.Reconstruction.VirtualElectrodes;
using Utility.Exports;
using Utility.Rendering;

namespace DataAccessLayer;

public sealed class ReconstructionExportRepository : IReconstructionExportRepository
{
    public DataExportResult ExportReconstructionData(ReconstructionExportRequest request)
    {
        try
        {
            Directory.CreateDirectory(request.TargetDirectory);

            var snapshots = CreateIterationSnapshots(request.Results);
            if (snapshots.Count == 0)
                return DataExportResult.CreateFailure("No Results", "No reconstruction iterations were recorded.");

            var firstSnapshot = snapshots.First();
            var latestSnapshot = snapshots.Last();

            var initialDistribution = request.InitialDistribution;
            var initialResult = CreateInitialDistributionResult(firstSnapshot.Result, initialDistribution);

            var originalMetadata = CreateRangeMetadata(latestSnapshot.Result.OriginalConductivityDistribution);
            AppendSetupMetadata(originalMetadata, request.MeasurementPattern, request.Configuration);

            var originalDiscretization = ResolveDiscretization(latestSnapshot.Result, request.Discretization);

            ReconstructionVideoRenderer.SaveDistributionSnapshot(request.TargetDirectory,
                                     "original_distribution.png",
                                     originalDiscretization,
                                     GetFrameForResult(latestSnapshot.Result, request.RenderFrame),
                                     latestSnapshot.Result,
                                     ReconstructionVideoRenderer.DistributionSnapshotType.Original,
                                     "Original Conductivity Distribution",
                                     originalMetadata,
                                     request.PotentialDisplayMode,
                                     request.ConductivityDisplayMode);

            var initialMetadata = CreateRangeMetadata(initialDistribution);
            double initialResidual = ReconstructionStatistics.CalculateResidual(initialResult, true);
            initialMetadata.Add(("Residual L2", FormatDouble(initialResidual)));

            var initialMetrics = ReconstructionStatistics.ComputeDistributionMetrics(initialResult, CancellationToken.None, false);
            if (initialMetrics.HasValue)
            {
                var metrics = initialMetrics.Value;
                initialMetadata.Add(("MAE", FormatDouble(metrics.Mae)));
                initialMetadata.Add(("RMSE", FormatDouble(metrics.Rmse)));
            }
            AppendSetupMetadata(initialMetadata, request.MeasurementPattern, request.Configuration);

            var initialDiscretization = ResolveDiscretization(initialResult, request.Discretization);

            ReconstructionVideoRenderer.SaveDistributionSnapshot(request.TargetDirectory,
                                     "initial_distribution.png",
                                     initialDiscretization,
                                     GetFrameForResult(firstSnapshot.Result, request.RenderFrame),
                                     initialResult,
                                     ReconstructionVideoRenderer.DistributionSnapshotType.Initial,
                                     "Initial Conductivity Distribution",
                                     initialMetadata,
                                     request.PotentialDisplayMode,
                                     request.ConductivityDisplayMode);

            var finalMetadata = CreateRangeMetadata(latestSnapshot.Result.ReconstructedConductivityDistribution);
            finalMetadata.Insert(0, ("Iteration", latestSnapshot.Iteration.ToString(CultureInfo.InvariantCulture)));
            finalMetadata.Add(("Residual L2", FormatDouble(latestSnapshot.Residual)));
            if (latestSnapshot.Metrics.HasValue)
            {
                var metrics = latestSnapshot.Metrics.Value;
                finalMetadata.Add(("MAE", FormatDouble(metrics.Mae)));
                finalMetadata.Add(($"RMSE", FormatDouble(metrics.Rmse)));
                finalMetadata.Add(("SSIM", FormatDouble(metrics.Ssim)));
            }
            finalMetadata.Add(("Correlation", FormatDouble(latestSnapshot.Correlation)));
            AppendSetupMetadata(finalMetadata, request.MeasurementPattern, request.Configuration);

            var latestDiscretization = ResolveDiscretization(latestSnapshot.Result, request.Discretization);

            ReconstructionVideoRenderer.SaveDistributionSnapshot(request.TargetDirectory,
                                     "reconstructed_distribution_latest.png",
                                     latestDiscretization,
                                     GetFrameForResult(latestSnapshot.Result, request.RenderFrame),
                                     latestSnapshot.Result,
                                     ReconstructionVideoRenderer.DistributionSnapshotType.Reconstructed,
                                     "Latest Reconstructed Conductivity",
                                     finalMetadata,
                                     request.PotentialDisplayMode,
                                     request.ConductivityDisplayMode);

            SaveBestMetricSnapshots(request.TargetDirectory,
                                     request.Discretization,
                                     request.RenderFrame,
                                     snapshots,
                                     request.PotentialDisplayMode,
                                     request.ConductivityDisplayMode,
                                     request.MeasurementPattern,
                                     request.Configuration);

            WriteIterationMetricsCsv(request.TargetDirectory, snapshots);
            WriteGradientMetricsCsv(request.TargetDirectory, request.Frames);
            WriteConfigurationSnapshot(request.TargetDirectory, request.Configuration);

            ExportMeshArtifacts(request.TargetDirectory, request.Discretization, latestSnapshot.Result);

            return DataExportResult.CreateSuccess(request.TargetDirectory);
        }
        catch (Exception ex)
        {
            return DataExportResult.CreateFailure("Export Failed", ex.Message);
        }
    }

    private static void WriteConfigurationSnapshot(string directory, ReconstructionConfigurationSnapshot configuration)
    {
        if (configuration == null)
            return;

        var options = new JsonSerializerOptions
        {
            WriteIndented = true
        };

        string json = JsonSerializer.Serialize(configuration, options);
        string path = Path.Combine(directory, "reconstruction_configuration.json");
        File.WriteAllText(path, json);
    }

    private static List<ReconstructionIterationSnapshot> CreateIterationSnapshots(IReadOnlyList<ReconstructionResult> results)
    {
        var snapshots = new List<ReconstructionIterationSnapshot>(results.Count);
        for (int i = 0; i < results.Count; i++)
        {
            var result = results[i];
            var metrics = ReconstructionStatistics.ComputeDistributionMetrics(result, CancellationToken.None, false);
            double residualValue = ReconstructionStatistics.CalculateResidual(result, true);
            double correlationValue = ReconstructionStatistics.CalculateCorrelation(result.ReconstructedConductivityDistribution,
                                                                                   result.OriginalConductivityDistribution,
                                                                                   true);
            snapshots.Add(new ReconstructionIterationSnapshot(i + 1, result, metrics, residualValue, correlationValue));
        }

        return snapshots;
    }

    private static void SaveBestMetricSnapshots(string directory,
                                                IDiscretization fallbackDiscretization,
                                                ReconstructionFrame fallbackFrame,
                                                IReadOnlyList<ReconstructionIterationSnapshot> snapshots,
                                                PotentialDisplayMode potentialMode,
                                                ConductivityDisplayMode conductivityMode,
                                                MeasurementPattern? measurementPattern,
                                                ReconstructionConfigurationSnapshot configuration)
    {
        var metricSnapshots = snapshots.Where(s => s.Metrics.HasValue).ToList();

        if (metricSnapshots.Count > 0)
        {
            var maeSnapshot = metricSnapshots
                .Where(s => !double.IsNaN(s.Metrics!.Value.Mae))
                .OrderBy(s => s.Metrics!.Value.Mae)
                .FirstOrDefault();

            if (maeSnapshot.Result != null)
            {
                var snapshotDiscretization = maeSnapshot.Result.Discretization ?? fallbackDiscretization;

                var metrics = maeSnapshot.Metrics!.Value;
                var metadata = new List<(string, string)>
                {
                    ("Iteration", maeSnapshot.Iteration.ToString(CultureInfo.InvariantCulture)),
                    ("MAE", FormatDouble(metrics.Mae)),
                    ("RMSE", FormatDouble(metrics.Rmse)),
                    ("SSIM", FormatDouble(metrics.Ssim)),
                    ("Residual L2", FormatDouble(maeSnapshot.Residual)),
                    ("Correlation", FormatDouble(maeSnapshot.Correlation))
                };

                AppendSetupMetadata(metadata, measurementPattern, configuration);

                ReconstructionVideoRenderer.SaveDistributionSnapshot(directory,
                                         "best_mae_distribution.png",
                                         snapshotDiscretization,
                                         GetFrameForResult(maeSnapshot.Result, fallbackFrame),
                                         maeSnapshot.Result,
                                         ReconstructionVideoRenderer.DistributionSnapshotType.Reconstructed,
                                         "Reconstruction – Min MAE",
                                         metadata,
                                         potentialMode,
                                         conductivityMode);
            }

            var ssimSnapshot = metricSnapshots
                .Where(s => !double.IsNaN(s.Metrics!.Value.Ssim))
                .OrderByDescending(s => s.Metrics!.Value.Ssim)
                .FirstOrDefault();

            if (ssimSnapshot.Result != null)
            {
                var snapshotDiscretization = ssimSnapshot.Result.Discretization ?? fallbackDiscretization;

                var metrics = ssimSnapshot.Metrics!.Value;
                var metadata = new List<(string, string)>
                {
                    ("Iteration", ssimSnapshot.Iteration.ToString(CultureInfo.InvariantCulture)),
                    ("SSIM", FormatDouble(metrics.Ssim)),
                    ("MAE", FormatDouble(metrics.Mae)),
                    ("RMSE", FormatDouble(metrics.Rmse)),
                    ("Residual L2", FormatDouble(ssimSnapshot.Residual)),
                    ("Correlation", FormatDouble(ssimSnapshot.Correlation))
                };

                AppendSetupMetadata(metadata, measurementPattern, configuration);

                ReconstructionVideoRenderer.SaveDistributionSnapshot(directory,
                                         "best_ssim_distribution.png",
                                         snapshotDiscretization,
                                         GetFrameForResult(ssimSnapshot.Result, fallbackFrame),
                                         ssimSnapshot.Result,
                                         ReconstructionVideoRenderer.DistributionSnapshotType.Reconstructed,
                                         "Reconstruction – Max SSIM",
                                         metadata,
                                         potentialMode,
                                         conductivityMode);
            }
        }

        var residualSnapshot = snapshots
            .OrderBy(s => s.Residual)
            .FirstOrDefault();

        if (residualSnapshot.Result != null)
        {
            var snapshotDiscretization = residualSnapshot.Result.Discretization ?? fallbackDiscretization;

            var metadata = new List<(string, string)>
            {
                ("Iteration", residualSnapshot.Iteration.ToString(CultureInfo.InvariantCulture)),
                ("Residual L2", FormatDouble(residualSnapshot.Residual)),
                ("Correlation", FormatDouble(residualSnapshot.Correlation))
            };

            if (residualSnapshot.Metrics.HasValue)
            {
                var metrics = residualSnapshot.Metrics.Value;
                metadata.Add(("MAE", FormatDouble(metrics.Mae)));
                metadata.Add(("RMSE", FormatDouble(metrics.Rmse)));
                metadata.Add(("SSIM", FormatDouble(metrics.Ssim)));
            }

            AppendSetupMetadata(metadata, measurementPattern, configuration);

            ReconstructionVideoRenderer.SaveDistributionSnapshot(directory,
                                         "best_residual_distribution.png",
                                         snapshotDiscretization,
                                         GetFrameForResult(residualSnapshot.Result, fallbackFrame),
                                         residualSnapshot.Result,
                                         ReconstructionVideoRenderer.DistributionSnapshotType.Reconstructed,
                                         "Reconstruction – Min Residual",
                                         metadata,
                                         potentialMode,
                                         conductivityMode);
        }

        var correlationSnapshot = snapshots
            .Where(s => !double.IsNaN(s.Correlation))
            .OrderByDescending(s => s.Correlation)
            .FirstOrDefault();

        if (correlationSnapshot.Result != null)
        {
            var snapshotDiscretization = correlationSnapshot.Result.Discretization ?? fallbackDiscretization;

            var metadata = new List<(string, string)>
            {
                ("Iteration", correlationSnapshot.Iteration.ToString(CultureInfo.InvariantCulture)),
                ("Correlation", FormatDouble(correlationSnapshot.Correlation)),
                ("Residual L2", FormatDouble(correlationSnapshot.Residual))
            };

            if (correlationSnapshot.Metrics.HasValue)
            {
                var metrics = correlationSnapshot.Metrics.Value;
                metadata.Add(("MAE", FormatDouble(metrics.Mae)));
                metadata.Add(("RMSE", FormatDouble(metrics.Rmse)));
                metadata.Add(("SSIM", FormatDouble(metrics.Ssim)));
            }

            AppendSetupMetadata(metadata, measurementPattern, configuration);

            ReconstructionVideoRenderer.SaveDistributionSnapshot(directory,
                                         "best_correlation_distribution.png",
                                         snapshotDiscretization,
                                         GetFrameForResult(correlationSnapshot.Result, fallbackFrame),
                                         correlationSnapshot.Result,
                                         ReconstructionVideoRenderer.DistributionSnapshotType.Reconstructed,
                                         "Reconstruction – Max Correlation",
                                         metadata,
                                         potentialMode,
                                         conductivityMode);
        }
    }

    private static ReconstructionResult CreateInitialDistributionResult(ReconstructionResult snapshot,
                                                                        ConductivityDistribution initialDistribution)
    {
        var discretization = snapshot.GetDiscretization();
        if (discretization != null)
        {
            return new ReconstructionResult(discretization,
                                            snapshot.OriginalConductivityDistribution,
                                            initialDistribution,
                                            initialDistribution,
                                            snapshot.Frames);
        }

        return new ReconstructionResult(snapshot.OriginalConductivityDistribution,
                                        initialDistribution,
                                        initialDistribution,
                                        snapshot.Frames);
    }

    private static void WriteIterationMetricsCsv(string directory,
                                                 IReadOnlyList<ReconstructionIterationSnapshot> snapshots)
    {
        string path = Path.Combine(directory, "iteration_metrics.csv");
        using var writer = new StreamWriter(path, false, Encoding.UTF8);
        writer.WriteLine("Iteration,Residual,RMSE,MAE,MAPE,PSNR,SSIM,Correlation,RMSEImprovement,MAEImprovement");

        foreach (var snapshot in snapshots)
        {
            var metrics = snapshot.Metrics;
            string residual = FormatCsvValue(snapshot.Residual);
            string rmse = metrics.HasValue ? FormatCsvValue(metrics.Value.Rmse) : string.Empty;
            string mae = metrics.HasValue ? FormatCsvValue(metrics.Value.Mae) : string.Empty;
            string mape = metrics.HasValue ? FormatCsvValue(metrics.Value.Mape) : string.Empty;
            string psnr = metrics.HasValue ? FormatCsvValue(metrics.Value.Psnr) : string.Empty;
            string ssim = metrics.HasValue ? FormatCsvValue(metrics.Value.Ssim) : string.Empty;
            string correlation = FormatCsvValue(snapshot.Correlation);
            string rmseImprovement = metrics.HasValue ? FormatCsvValue(metrics.Value.RmseImprovement) : string.Empty;
            string maeImprovement = metrics.HasValue ? FormatCsvValue(metrics.Value.MaeImprovement) : string.Empty;

            writer.WriteLine(string.Join(',',
                snapshot.Iteration.ToString(CultureInfo.InvariantCulture),
                residual,
                rmse,
                mae,
                mape,
                psnr,
                ssim,
                correlation,
                rmseImprovement,
                maeImprovement));
        }
    }

    private static void WriteGradientMetricsCsv(string directory, IReadOnlyList<ReconstructionFrame> frames)
    {
        string path = Path.Combine(directory, "gradient_metrics.csv");
        using var writer = new StreamWriter(path, false, Encoding.UTF8);
        writer.WriteLine("Frame,GradientNorm,GradientAngleDegrees");

        Dictionary<int, double>? previous = null;

        for (int i = 0; i < frames.Count; i++)
        {
            var gradient = frames[i].ConductivityGradient.Conductivities;
            double norm = 0.0;
            foreach (var kv in gradient)
            {
                norm += kv.Value * kv.Value;
            }
            norm = Math.Sqrt(norm);

            double? angle = null;
            if (previous != null && previous.Count > 0 && gradient.Count > 0)
            {
                angle = ReconstructionStatistics.ComputeGradientAngle(previous, gradient);
                if (double.IsNaN(angle.Value))
                    angle = null;
            }

            string angleValue = angle.HasValue ? FormatCsvValue(angle.Value) : string.Empty;
            writer.WriteLine(string.Join(',',
                (i + 1).ToString(CultureInfo.InvariantCulture),
                FormatCsvValue(norm),
                angleValue));

            previous = new Dictionary<int, double>(gradient);
        }
    }

    private static ReconstructionFrame GetFrameForResult(ReconstructionResult result, ReconstructionFrame fallback)
        => result.Frames.LastOrDefault() ?? fallback;

    private static IDiscretization ResolveDiscretization(ReconstructionResult result, IDiscretization fallback)
        => result.Discretization ?? fallback;

    private static List<(string Label, string Value)> CreateRangeMetadata(ConductivityDistribution distribution)
    {
        var stats = ComputeStatistics(distribution.Conductivities);
        return new List<(string, string)>
        {
            ("Minimum σ", FormatNullableDouble(stats.Min)),
            ("Maximum σ", FormatNullableDouble(stats.Max)),
            ("Mean σ", FormatNullableDouble(stats.Mean))
        };
    }

    private static void AppendSetupMetadata(List<(string Label, string Value)> metadata,
                                            MeasurementPattern? pattern,
                                            ReconstructionConfigurationSnapshot? configuration)
    {
        metadata.Add(("Virtual Electrodes", DescribeVirtualElectrodeSettings(configuration?.VirtualElectrodes)));
        metadata.Add(("Measurement Mode", DescribeMeasurementPattern(pattern)));
    }

    private static void ExportMeshArtifacts(string directory, IDiscretization discretization, ReconstructionResult latestResult)
    {
        try
        {
            if (discretization is FEMMesh fem)
            {
                var timestamp = DateTime.UtcNow;
                string baseName = discretization.Metadata?.Generator ?? "reconstruction_mesh";
                baseName = string.IsNullOrWhiteSpace(baseName)
                    ? "reconstruction_mesh"
                    : string.Join("_", baseName.Split(Path.GetInvalidFileNameChars()));

                var vertexOrder = new List<int>(fem.Vertices.Count);
                string stlPath = Path.Combine(directory, $"{baseName}_{timestamp:yyyyMMdd_HHmmss}.stl");
                SaveFemMeshAsStl(fem, stlPath, vertexOrder);

                var conductivitySource = latestResult.ReconstructedConductivityDistribution
                                         ?? discretization.GetConductivityDistribution();

                var export = new
                {
                    stlPath = Path.GetFileName(stlPath),
                    vertexOrder,
                    elementConductivities = conductivitySource.Conductivities
                };

                var options = new JsonSerializerOptions { WriteIndented = true };
                string jsonPath = Path.ChangeExtension(stlPath, ".json")
                                   ?? Path.Combine(directory, $"{baseName}_{timestamp:yyyyMMdd_HHmmss}.json");
                File.WriteAllText(jsonPath, JsonSerializer.Serialize(export, options));
            }
            else if (discretization is LBMGrid lbm)
            {
                var conductivitySource = latestResult.ReconstructedConductivityDistribution
                                         ?? discretization.GetConductivityDistribution();

                var export = new
                {
                    grid = new { lbm.Nx, lbm.Ny },
                    elementConductivities = conductivitySource.Conductivities
                };

                var options = new JsonSerializerOptions { WriteIndented = true };
                string jsonPath = Path.Combine(directory, "lbm_mesh.json");
                File.WriteAllText(jsonPath, JsonSerializer.Serialize(export, options));
            }
        }
        catch
        {
            // Mesh export should not block the primary reconstruction export flow.
        }
    }

    private static void SaveFemMeshAsStl(FEMMesh mesh, string file, IList<int> stlVertexOrder)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(file) ?? string.Empty);

        HashSet<int> seenVertices = new();
        using var writer = new StreamWriter(file, false, new System.Text.UTF8Encoding(false));

        string safeName = Path.GetFileNameWithoutExtension(file);
        writer.WriteLine($"solid {safeName}");

        var elements = mesh.ElementsTyped
            .OrderBy(e => e.Id)
            .ToList();

        foreach (var element in elements)
        {
            var a = element.Vertices[0];
            var b = element.Vertices[1];
            var c = element.Vertices[2];

            var normal = CalculateFacetNormal(a, b, c);
            writer.WriteLine(string.Format(CultureInfo.InvariantCulture,
                "  facet normal {0:G17} {1:G17} {2:G17}", normal.X, normal.Y, normal.Z));
            writer.WriteLine("    outer loop");
            WriteVertex(writer, a);
            WriteVertex(writer, b);
            WriteVertex(writer, c);
            writer.WriteLine("    endloop");
            writer.WriteLine("  endfacet");
        }

        writer.WriteLine("endsolid");

        (double X, double Y, double Z) CalculateFacetNormal(FEMVertex a, FEMVertex b, FEMVertex c)
        {
            double ux = b.X - a.X;
            double uy = b.Y - a.Y;
            double vx = c.X - a.X;
            double vy = c.Y - a.Y;

            double nx = 0.0;
            double ny = 0.0;
            double nz = ux * vy - uy * vx;

            double length = Math.Sqrt(nx * nx + ny * ny + nz * nz);
            if (length < 1e-12)
                return (0.0, 0.0, 1.0);

            return (nx / length, ny / length, nz / length);
        }

        void WriteVertex(StreamWriter writer, FEMVertex vertex)
        {
            if (seenVertices.Add(vertex.GlobalId))
                stlVertexOrder.Add(vertex.GlobalId);

            writer.WriteLine(string.Format(CultureInfo.InvariantCulture,
                "      vertex {0:G17} {1:G17} 0", vertex.X, vertex.Y));
        }
    }

    private static string DescribeMeasurementPattern(MeasurementPattern? pattern)
    {
        if (pattern == null)
            return "Unknown";

        string representation = pattern.Representation == MeasurementRepresentation.PotentialDifference
            ? "Potential difference"
            : "Amplitude";
        string setup = pattern.MeasurementSetup == ElectrodeMeasurementSetup.NonActive
            ? "Non-active electrodes"
            : "Active electrodes";

        return $"{representation}\n{setup}";
    }

    private static string DescribeVirtualElectrodeSettings(VirtualElectrodeConfigurationSnapshot? snapshot)
    {
        if (snapshot == null)
            return "Unknown";

        if (!snapshot.UseVirtualElectrodes)
            return "Disabled";

        var lines = new List<string>
        {
            "Enabled",
            $"Method: {GetVirtualElectrodeMethodDisplayName(snapshot.Method)}",
            $"Per Gap: {snapshot.VirtualElectrodesPerGap.ToString(CultureInfo.InvariantCulture)}"
        };

        switch (snapshot.Method)
        {
            case VirtualElectrodeMethod.LinearCombination:
                lines.Add($"Alpha: {FormatDouble(snapshot.LinearCombinationAlpha)}");
                break;
            case VirtualElectrodeMethod.HarrachSensitivityInterpolation:
                lines.Add($"Lambda: {FormatDouble(snapshot.HarrachLambda)}");
                break;
            case VirtualElectrodeMethod.NdMapSpectralInterpolation:
                lines.Add($"Max Mode: {snapshot.NdMaxMode.ToString(CultureInfo.InvariantCulture)}");
                break;
        }

        return string.Join('\n', lines);
    }

    private static string GetVirtualElectrodeMethodDisplayName(VirtualElectrodeMethod method)
        => method switch
        {
            VirtualElectrodeMethod.GeometricInterpolation => "Geometric Interpolation",
            VirtualElectrodeMethod.LinearCombination => "Linear Combination",
            VirtualElectrodeMethod.HarrachSensitivityInterpolation => "Harrach Sensitivity",
            VirtualElectrodeMethod.NdMapSpectralInterpolation => "ND Map Spectral",
            _ => "None"
        };

    private static string FormatCsvValue(double value)
    {
        if (double.IsNaN(value))
            return string.Empty;
        if (double.IsPositiveInfinity(value))
            return "Infinity";
        if (double.IsNegativeInfinity(value))
            return "-Infinity";
        return value.ToString("G17", CultureInfo.InvariantCulture);
    }

    private static string FormatDouble(double value, string format = "F3")
        => value.ToString(format, CultureInfo.InvariantCulture);

    private static string FormatNullableDouble(double? value, string format = "F3")
        => value.HasValue ? FormatDouble(value.Value, format) : "—";

    private static (double? Min, double? Max, double? Mean) ComputeStatistics(IReadOnlyDictionary<int, double> values)
    {
        if (values == null || values.Count == 0)
            return (null, null, null);

        double min = double.PositiveInfinity;
        double max = double.NegativeInfinity;
        double sum = 0.0;
        int count = 0;

        foreach (var value in values.Values)
        {
            if (double.IsNaN(value) || double.IsInfinity(value))
                continue;

            if (value < min)
                min = value;
            if (value > max)
                max = value;

            sum += value;
            count++;
        }

        if (count == 0)
            return (null, null, null);

        double mean = sum / count;
        return (min, max, mean);
    }

    private readonly record struct ReconstructionIterationSnapshot(int Iteration,
                                                                    ReconstructionResult Result,
                                                                    DistributionMetrics? Metrics,
                                                                    double Residual,
                                                                    double Correlation);
}
