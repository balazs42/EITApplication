using MathNet.Numerics.LinearAlgebra;
using Utility.Classes.Discretizer;

namespace Utility.Classes.Reconstruction.VirtualElectrodes.Estimators
{
    /// <summary>
    /// Non-parametric Gaussian Process (GP) regression on the circle using a periodic distance
    /// induced by wrapping the angular difference.
    ///
    /// Kernel: k(θ_i, θ_j) = σ_f² exp(-0.5 * d(θ_i, θ_j)^2 / ℓ²), where d is the shortest arc distance.
    /// Noise:  add σ_n² to diagonal.
    /// Solve:  α = K^{-1} y, predict via k_*^T α for any angle.
    /// </summary>
    internal sealed class GaussianProcessVirtualElectrodeEstimator : IVirtualElectrodeEstimator
    {
        /// <inheritdoc />
        public double[] CompleteElectrodePotentials(IReadOnlyList<Electrode> electrodes, double[] measuredVoltages, VirtualElectrodeSettings settings, ForwardModelContext? forwardContext = null)
        {
            try
            {
                var values = VirtualElectrodeHelpers.BuildMeasuredLookup(electrodes, measuredVoltages);
                var (realOrder, angles) = VirtualElectrodeHelpers.PrepareOrdering(electrodes, forwardContext);
                if (realOrder.Count == 0)
                {
                    var fallback = new GeometricVirtualElectrodeEstimator();
                    return fallback.CompleteElectrodePotentials(electrodes, measuredVoltages, settings, forwardContext);
                }

                int n = realOrder.Count;
                var y = Vector<double>.Build.Dense(n);
                var realAngles = new double[n];

                // Collect training inputs/outputs
                for (int i = 0; i < n; i++)
                {
                    var (id, angle) = realOrder[i];
                    realAngles[i] = angle;
                    y[i] = values.TryGetValue(id, out var mv) ? mv : 0.0;
                }

                // Hyperparameters with minimum safeguards
                double sigmaF2 = Math.Max(settings.GpSignalVariance, 1e-12);
                double lengthScale = Math.Max(settings.GpLengthScale, 1e-6);
                double sigmaN2 = Math.Max(settings.GpNoiseVariance, 1e-12);

                // Build covariance matrix K
                var kernel = Matrix<double>.Build.Dense(n, n);
                for (int i = 0; i < n; i++)
                {
                    for (int j = 0; j < n; j++)
                    {
                        double diff = Math.Abs(realAngles[i] - realAngles[j]);
                        double distance = Math.Min(diff, 2.0 * Math.PI - diff); // shortest circular arc
                        double ratio = distance / lengthScale;
                        double value = sigmaF2 * Math.Exp(-0.5 * ratio * ratio);
                        if (i == j)
                            value += sigmaN2; // add noise on the diagonal
                        kernel[i, j] = value;
                    }
                }

                // Compute α = K^{-1} y
                var alpha = kernel.Solve(y);

                bool invalid = false;
                foreach (var electrode in electrodes)
                {
                    // Keep original measured value for real electrodes if already present
                    if (!electrode.IsVirtual && values.ContainsKey(electrode.Id))
                        continue;

                    double angle = angles[electrode.Id];
                    var kStar = Vector<double>.Build.Dense(n);
                    for (int i = 0; i < n; i++)
                    {
                        double diff = Math.Abs(angle - realAngles[i]);
                        double distance = Math.Min(diff, 2.0 * Math.PI - diff);
                        double ratio = distance / lengthScale;
                        kStar[i] = sigmaF2 * Math.Exp(-0.5 * ratio * ratio);
                    }

                    double prediction = kStar.DotProduct(alpha);
                    if (double.IsNaN(prediction) || double.IsInfinity(prediction))
                    {
                        invalid = true;
                        break;
                    }

                    values[electrode.Id] = prediction;
                }

                if (invalid)
                {
                    var fallback = new GeometricVirtualElectrodeEstimator();
                    return fallback.CompleteElectrodePotentials(electrodes, measuredVoltages, settings, forwardContext);
                }

                return VirtualElectrodeHelpers.MergeVoltages(electrodes, values);
            }
            catch
            {
                var fallback = new GeometricVirtualElectrodeEstimator();
                return fallback.CompleteElectrodePotentials(electrodes, measuredVoltages, settings, forwardContext);
            }
        }
    }
}
