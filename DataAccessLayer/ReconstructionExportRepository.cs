using SkiaSharp;
using System.Globalization;
using System.Text;
using Utility.Classes;
using Utility.Classes.Discretizer;
using Utility.Classes.Measurement;
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

            ReconstructionVideoRenderer.SaveDistributionSnapshot(request.TargetDirectory,
                                     "original_distribution.png",
                                     request.Discretization,
                                     GetFrameForResult(latestSnapshot.Result, request.RenderFrame),
                                     latestSnapshot.Result,
                                     ReconstructionVideoRenderer.DistributionSnapshotType.Original,
                                     "Original Conductivity Distribution",
                                     CreateRangeMetadata(latestSnapshot.Result.OriginalConductivityDistribution),
                                     request.DisplayMode);

            var initialMetadata = CreateRangeMetadata(initialDistribution);
            double initialResidual = CalculateResidual(initialDistribution,
                                                       firstSnapshot.Result.OriginalConductivityDistribution);
            initialMetadata.Add(("Residual L2", FormatDouble(initialResidual)));

            var initialMetrics = ComputeDistributionMetrics(initialResult, CancellationToken.None);
            if (initialMetrics.HasValue)
            {
                var metrics = initialMetrics.Value;
                initialMetadata.Add(("MAE", FormatDouble(metrics.Mae)));
                initialMetadata.Add(("RMSE", FormatDouble(metrics.Rmse)));
            }
            initialMetadata.Add(("Measurement Mode", DescribeMeasurementPattern(request.MeasurementPattern)));

            ReconstructionVideoRenderer.SaveDistributionSnapshot(request.TargetDirectory,
                                     "initial_distribution.png",
                                     request.Discretization,
                                     GetFrameForResult(firstSnapshot.Result, request.RenderFrame),
                                     initialResult,
                                     ReconstructionVideoRenderer.DistributionSnapshotType.Initial,
                                     "Initial Conductivity Distribution",
                                     initialMetadata,
                                     request.DisplayMode);

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
            finalMetadata.Add(("Measurement Mode", DescribeMeasurementPattern(request.MeasurementPattern)));

            ReconstructionVideoRenderer.SaveDistributionSnapshot(request.TargetDirectory,
                                     "reconstructed_distribution_latest.png",
                                     request.Discretization,
                                     GetFrameForResult(latestSnapshot.Result, request.RenderFrame),
                                     latestSnapshot.Result,
                                     ReconstructionVideoRenderer.DistributionSnapshotType.Reconstructed,
                                     "Latest Reconstructed Conductivity",
                                     finalMetadata,
                                     request.DisplayMode);

            SaveBestMetricSnapshots(request.TargetDirectory,
                                     request.Discretization,
                                     request.RenderFrame,
                                     snapshots,
                                     request.DisplayMode);

            WriteIterationMetricsCsv(request.TargetDirectory, snapshots);
            WriteGradientMetricsCsv(request.TargetDirectory, request.Frames);

            return DataExportResult.CreateSuccess(request.TargetDirectory);
        }
        catch (Exception ex)
        {
            return DataExportResult.CreateFailure("Export Failed", ex.Message);
        }
    }

    private static List<ReconstructionIterationSnapshot> CreateIterationSnapshots(IReadOnlyList<ReconstructionResult> results)
    {
        var snapshots = new List<ReconstructionIterationSnapshot>(results.Count);
        for (int i = 0; i < results.Count; i++)
        {
            var result = results[i];
            var metrics = ComputeDistributionMetrics(result, CancellationToken.None);
            double residualValue = CalculateResidual(result.ReconstructedConductivityDistribution,
                                                     result.OriginalConductivityDistribution);
            double correlationValue = CalculateCorrelation(result.ReconstructedConductivityDistribution,
                                                           result.OriginalConductivityDistribution);
            snapshots.Add(new ReconstructionIterationSnapshot(i + 1, result, metrics, residualValue, correlationValue));
        }

        return snapshots;
    }

    private static void SaveBestMetricSnapshots(string directory,
                                                IDiscretization discretization,
                                                ReconstructionFrame fallbackFrame,
                                                IReadOnlyList<ReconstructionIterationSnapshot> snapshots,
                                                PotentialDisplayMode mode)
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

                ReconstructionVideoRenderer.SaveDistributionSnapshot(directory,
                                         "best_mae_distribution.png",
                                         discretization,
                                         GetFrameForResult(maeSnapshot.Result, fallbackFrame),
                                         maeSnapshot.Result,
                                         ReconstructionVideoRenderer.DistributionSnapshotType.Reconstructed,
                                         "Reconstruction – Min MAE",
                                         metadata,
                                         mode);
            }

            var ssimSnapshot = metricSnapshots
                .Where(s => !double.IsNaN(s.Metrics!.Value.Ssim))
                .OrderByDescending(s => s.Metrics!.Value.Ssim)
                .FirstOrDefault();

            if (ssimSnapshot.Result != null)
            {
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

                ReconstructionVideoRenderer.SaveDistributionSnapshot(directory,
                                         "best_ssim_distribution.png",
                                         discretization,
                                         GetFrameForResult(ssimSnapshot.Result, fallbackFrame),
                                         ssimSnapshot.Result,
                                         ReconstructionVideoRenderer.DistributionSnapshotType.Reconstructed,
                                         "Reconstruction – Max SSIM",
                                         metadata,
                                         mode);
            }
        }

        var residualSnapshot = snapshots
            .OrderBy(s => s.Residual)
            .FirstOrDefault();

        if (residualSnapshot.Result != null)
        {
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

            ReconstructionVideoRenderer.SaveDistributionSnapshot(directory,
                                     "best_residual_distribution.png",
                                     discretization,
                                     GetFrameForResult(residualSnapshot.Result, fallbackFrame),
                                     residualSnapshot.Result,
                                     ReconstructionVideoRenderer.DistributionSnapshotType.Reconstructed,
                                     "Reconstruction – Min Residual",
                                     metadata,
                                     mode);
        }

        var correlationSnapshot = snapshots
            .Where(s => !double.IsNaN(s.Correlation))
            .OrderByDescending(s => s.Correlation)
            .FirstOrDefault();

        if (correlationSnapshot.Result != null)
        {
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

            ReconstructionVideoRenderer.SaveDistributionSnapshot(directory,
                                     "best_correlation_distribution.png",
                                     discretization,
                                     GetFrameForResult(correlationSnapshot.Result, fallbackFrame),
                                     correlationSnapshot.Result,
                                     ReconstructionVideoRenderer.DistributionSnapshotType.Reconstructed,
                                     "Reconstruction – Max Correlation",
                                     metadata,
                                     mode);
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
                angle = ComputeGradientAngle(previous, gradient);
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

    private static string DescribeMeasurementPattern(MeasurementPattern? pattern)
    {
        if (pattern == null)
            return "Unknown";

        string representation = pattern.Representation == MeasurementRepresentation.PotentialDifference
            ? "Potential difference"
            : "Amplitude";
        string setup = pattern.MeasurementSetup == ElectrodeMeasurementSetup.NonActive
            ? "non-active electrodes"
            : "active electrodes";

        return $"{representation} - {setup}";
    }

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

    private static double CalculateResidual(ConductivityDistribution reconstructed, ConductivityDistribution original)
    {
        double sum = 0.0;
        foreach (var kv in reconstructed.Conductivities)
        {
            original.Conductivities.TryGetValue(kv.Key, out double origVal);
            double diff = kv.Value - origVal;
            sum += diff * diff;
        }
        return Math.Sqrt(sum);
    }

    private static double CalculateCorrelation(ConductivityDistribution reconstructed, ConductivityDistribution original)
    {
        if (reconstructed.Conductivities.Count == 0)
            return 0.0;

        double sumReconstructed = 0.0;
        double sumOriginal = 0.0;
        foreach (var kv in reconstructed.Conductivities)
        {
            sumReconstructed += kv.Value;
            original.Conductivities.TryGetValue(kv.Key, out double origVal);
            sumOriginal += origVal;
        }

        int count = reconstructed.Conductivities.Count;
        double meanReconstructed = sumReconstructed / count;
        double meanOriginal = sumOriginal / count;

        double numerator = 0.0;
        double sumSqReconstructed = 0.0;
        double sumSqOriginal = 0.0;
        foreach (var kv in reconstructed.Conductivities)
        {
            original.Conductivities.TryGetValue(kv.Key, out double origVal);
            double centeredReconstructed = kv.Value - meanReconstructed;
            double centeredOriginal = origVal - meanOriginal;
            numerator += centeredReconstructed * centeredOriginal;
            sumSqReconstructed += centeredReconstructed * centeredReconstructed;
            sumSqOriginal += centeredOriginal * centeredOriginal;
        }

        double denominator = Math.Sqrt(sumSqReconstructed) * Math.Sqrt(sumSqOriginal);
        if (denominator <= 1e-12)
            return double.NaN;

        double correlation = numerator / denominator;
        return double.IsNaN(correlation) ? double.NaN : correlation;
    }

    private static DistributionMetrics? ComputeDistributionMetrics(ReconstructionResult result, CancellationToken token)
    {
        var reconstructed = result.ReconstructedConductivityDistribution.Conductivities;
        if (reconstructed.Count == 0)
            return null;

        var original = result.OriginalConductivityDistribution.Conductivities;
        var initial = result.InitialConductivitiyDistribution.Conductivities;

        int count = reconstructed.Count;
        double[] recon = new double[count];
        double[] orig = new double[count];
        double[] init = new double[count];

        double sumSq = 0.0;
        double sumAbs = 0.0;
        double sumPct = 0.0;
        double maxAbs = 0.0;

        int index = 0;
        foreach (var kv in reconstructed)
        {
            token.ThrowIfCancellationRequested();

            double r = kv.Value;
            original.TryGetValue(kv.Key, out double o);
            initial.TryGetValue(kv.Key, out double i);

            recon[index] = r;
            orig[index] = o;
            init[index] = i;

            double diff = r - o;
            sumSq += diff * diff;
            sumAbs += Math.Abs(diff);
            sumPct += Math.Abs(diff) / Math.Max(Math.Abs(o), 1e-6);

            maxAbs = Math.Max(maxAbs, Math.Abs(o));
            maxAbs = Math.Max(maxAbs, Math.Abs(r));
            index++;
        }

        double mse = sumSq / Math.Max(count, 1);
        double rmse = Math.Sqrt(mse);
        double mae = sumAbs / Math.Max(count, 1);
        double mape = sumPct / Math.Max(count, 1);

        double psnr;
        if (mse <= 1e-12)
        {
            psnr = double.PositiveInfinity;
        }
        else
        {
            double peak = maxAbs <= 1e-12 ? 1.0 : maxAbs;
            psnr = 20.0 * Math.Log10(peak / Math.Sqrt(mse));
        }

        double initialRmse = 0.0;
        double initialMae = 0.0;
        for (int i = 0; i < count; i++)
        {
            double diff = init[i] - orig[i];
            initialRmse += diff * diff;
            initialMae += Math.Abs(diff);
        }

        initialRmse = Math.Sqrt(initialRmse / Math.Max(count, 1));
        initialMae /= Math.Max(count, 1);

        double rmseImprovement = initialRmse - rmse;
        double maeImprovement = initialMae - mae;

        double ssim = CalculateSsim(recon, orig);

        return new DistributionMetrics(rmse,
                                       mae,
                                       mape,
                                       psnr,
                                       ssim,
                                       rmseImprovement,
                                       maeImprovement);
    }

    private static double CalculateSsim(double[] recon, double[] orig)
    {
        if (recon.Length == 0 || orig.Length == 0)
            return double.NaN;

        double meanRecon = recon.Average();
        double meanOrig = orig.Average();

        double varianceRecon = 0.0;
        double varianceOrig = 0.0;
        double covariance = 0.0;

        for (int i = 0; i < recon.Length; i++)
        {
            double centeredRecon = recon[i] - meanRecon;
            double centeredOrig = orig[i] - meanOrig;
            varianceRecon += centeredRecon * centeredRecon;
            varianceOrig += centeredOrig * centeredOrig;
            covariance += centeredRecon * centeredOrig;
        }

        varianceRecon /= recon.Length;
        varianceOrig /= orig.Length;
        covariance /= recon.Length;

        const double c1 = 0.01 * 0.01;
        const double c2 = 0.03 * 0.03;

        double numerator = (2 * meanRecon * meanOrig + c1) * (2 * covariance + c2);
        double denominator = (meanRecon * meanRecon + meanOrig * meanOrig + c1) * (varianceRecon + varianceOrig + c2);

        return denominator <= 0.0 ? double.NaN : numerator / denominator;
    }

    private static double ComputeGradientAngle(Dictionary<int, double> previous, Dictionary<int, double> current)
    {
        double dot = 0.0;
        double prevNorm = 0.0;
        double currNorm = 0.0;

        foreach (var kv in current)
        {
            double value = kv.Value;
            currNorm += value * value;
            if (previous.TryGetValue(kv.Key, out double prevValue))
                dot += prevValue * value;
        }

        foreach (var kv in previous)
        {
            double value = kv.Value;
            prevNorm += value * value;
        }

        double denom = Math.Sqrt(prevNorm) * Math.Sqrt(currNorm);
        if (denom <= 1e-12)
            return double.NaN;

        double cosTheta = dot / denom;
        cosTheta = Math.Clamp(cosTheta, -1.0, 1.0);
        return Math.Acos(cosTheta) * (180.0 / Math.PI);
    }

    private readonly record struct DistributionMetrics(double Rmse,
                                                        double Mae,
                                                        double Mape,
                                                        double Psnr,
                                                        double Ssim,
                                                        double RmseImprovement,
                                                        double MaeImprovement);

    private readonly record struct ReconstructionIterationSnapshot(int Iteration,
                                                                    ReconstructionResult Result,
                                                                    DistributionMetrics? Metrics,
                                                                    double Residual,
                                                                    double Correlation);
}
