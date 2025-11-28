using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using Utility.Classes.ReconstructionParameters;

namespace Utility.Classes.Reconstruction.Metrics
{
    public static class ReconstructionStatistics
    {
        public static double CalculateResidual(ReconstructionResult result, bool scaleToMillivolts = false)
        {
            if (result.Frames.Count == 0)
                return 0.0;

            double sumSq = 0.0;
            int sampleCount = 0;

            foreach (var frame in result.Frames)
            {
                var measured = frame.MeasuredElectrodeValues;
                var simulated = frame.SimulatedElectrodeValues;

                if (measured == null || simulated == null)
                    continue;

                int length = Math.Min(measured.Length, simulated.Length);
                for (int i = 0; i < length; i++)
                {
                    double measuredValue = measured[i];
                    double simulatedValue = simulated[i];

                    if (double.IsNaN(measuredValue) || double.IsInfinity(measuredValue))
                        continue;
                    if (double.IsNaN(simulatedValue) || double.IsInfinity(simulatedValue))
                        continue;

                    double diff = simulatedValue - measuredValue;
                    sumSq += diff * diff;
                    sampleCount++;
                }
            }

            if (sampleCount == 0)
                return 0.0;

            double rms = Math.Sqrt(sumSq / sampleCount);
            return scaleToMillivolts ? rms * 1000.0 : rms;
        }

        public static double CalculateCorrelation(ConductivityDistribution reconstructed,
                                                  ConductivityDistribution original,
                                                  bool returnNaNOnDegenerate = false)
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
                return returnNaNOnDegenerate ? double.NaN : 0.0;

            double correlation = numerator / denominator;
            return double.IsNaN(correlation) && returnNaNOnDegenerate ? double.NaN : correlation;
        }

        public static DistributionMetrics? ComputeDistributionMetrics(ReconstructionResult result,
                                                                      CancellationToken token,
                                                                      bool useRelativeImprovement = true)
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
                psnr = double.PositiveInfinity;
            else
            {
                double peak = maxAbs <= 1e-12 ? 1.0 : maxAbs;
                psnr = 20.0 * Math.Log10(peak / Math.Sqrt(mse));
            }

            double initialRmse = 0.0;
            double initialMae = 0.0;
            for (int i = 0; i < count; i++)
            {
                token.ThrowIfCancellationRequested();
                double diffInit = init[i] - orig[i];
                initialRmse += diffInit * diffInit;
                initialMae += Math.Abs(diffInit);
            }

            initialRmse = Math.Sqrt(initialRmse / Math.Max(count, 1));
            initialMae /= Math.Max(count, 1);

            double rmseImprovement = useRelativeImprovement && initialRmse > 1e-9
                ? (initialRmse - rmse) / initialRmse
                : initialRmse - rmse;
            double maeImprovement = useRelativeImprovement && initialMae > 1e-9
                ? (initialMae - mae) / initialMae
                : initialMae - mae;

            double ssim = ComputeSsim(orig, recon);

            return new DistributionMetrics(rmse, mae, mape, psnr, ssim, rmseImprovement, maeImprovement);
        }

        public static double ComputeGradientAngle(Dictionary<int, double> previous, Dictionary<int, double> current)
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

        private static double ComputeSsim(IReadOnlyList<double> reference, IReadOnlyList<double> test)
        {
            if (reference.Count == 0 || reference.Count != test.Count)
                return double.NaN;

            double meanRef = 0.0;
            double meanTest = 0.0;
            for (int i = 0; i < reference.Count; i++)
            {
                meanRef += reference[i];
                meanTest += test[i];
            }

            int n = reference.Count;
            meanRef /= n;
            meanTest /= n;

            double varianceRef = 0.0;
            double varianceTest = 0.0;
            double covariance = 0.0;

            for (int i = 0; i < n; i++)
            {
                double refDelta = reference[i] - meanRef;
                double testDelta = test[i] - meanTest;
                varianceRef += refDelta * refDelta;
                varianceTest += testDelta * testDelta;
                covariance += refDelta * testDelta;
            }

            varianceRef /= n;
            varianceTest /= n;
            covariance /= n;

            const double c1 = 0.01 * 0.01;
            const double c2 = 0.03 * 0.03;

            double numerator = (2 * meanRef * meanTest + c1) * (2 * covariance + c2);
            double denominator = (meanRef * meanRef + meanTest * meanTest + c1) * (varianceRef + varianceTest + c2);

            if (denominator <= 1e-12)
                return double.NaN;

            return numerator / denominator;
        }
    }

    public readonly record struct DistributionMetrics(double Rmse,
                                                       double Mae,
                                                       double Mape,
                                                       double Psnr,
                                                       double Ssim,
                                                       double RmseImprovement,
                                                       double MaeImprovement);
}
