using MathNet.Numerics.LinearAlgebra;
using Utility.Classes.Discretizer;

namespace Utility.Classes.Reconstruction.VirtualElectrodes.Estimators
{

    /// <summary>
    /// Maximum-likelihood estimator under a Fourier basis model.
    ///
    /// Model:
    ///   y(θ) ≈ θ₀ + Σ_{k=1..K} [a_k cos(kθ) + b_k sin(kθ)]
    /// Fit:
    ///   θ̂ = argmin ||Φθ - y||² + λ||θ||² (ridge optional via <see cref="VirtualElectrodeSettings.MlRegularization"/>)
    /// Predict:
    ///   Evaluate model for all electrode angles; keep measured for real, fill virtual.
    /// Falls back to geometric if underdetermined or numerically invalid.
    /// </summary>
    internal sealed class MaximumLikelihoodVirtualElectrodeEstimator : IVirtualElectrodeEstimator
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

                int n = realOrder.Count;                 // number of observations
                int kMax = Math.Max(1, settings.FourierOrder);
                // Ensure we have enough equations: parameters = 1 + 2*K <= n
                while (1 + 2 * kMax > n && kMax > 1)
                    kMax--;

                if (1 + 2 * kMax > n)
                {
                    var fallback = new GeometricVirtualElectrodeEstimator();
                    return fallback.CompleteElectrodePotentials(electrodes, measuredVoltages, settings, forwardContext);
                }

                int parameterCount = 1 + 2 * kMax;
                var phi = Matrix<double>.Build.Dense(n, parameterCount); // design matrix
                var y = Vector<double>.Build.Dense(n);                           // measurements

                // Fill design matrix with Fourier features evaluated at real electrode angles
                for (int i = 0; i < n; i++)
                {
                    var (id, angle) = realOrder[i];
                    double measurement = values.TryGetValue(id, out var mv) ? mv : 0.0;
                    y[i] = measurement;
                    phi[i, 0] = 1.0; // bias term
                    for (int k = 1; k <= kMax; k++)
                    {
                        phi[i, k] = Math.Cos(k * angle);
                        phi[i, kMax + k] = Math.Sin(k * angle);
                    }
                }

                // Solve (ΦᵀΦ + λI) θ = Φᵀ y
                double lambda = Math.Max(settings.MlRegularization, 0.0);
                var lhs = phi.TransposeThisAndMultiply(phi);
                if (lambda > 0.0)
                    lhs += Matrix<double>.Build.DenseIdentity(parameterCount) * lambda;
                var rhs = phi.TransposeThisAndMultiply(y);
                var thetaHat = lhs.Solve(rhs);

                // Predict for all electrodes
                bool invalid = false;
                foreach (var electrode in electrodes)
                {
                    double angle = angles[electrode.Id];
                    double prediction = thetaHat[0];
                    for (int k = 1; k <= kMax; k++)
                    {
                        prediction += thetaHat[k] * Math.Cos(k * angle) + thetaHat[kMax + k] * Math.Sin(k * angle);
                    }

                    if (double.IsNaN(prediction) || double.IsInfinity(prediction))
                    {
                        invalid = true;
                        break;
                    }

                    if (electrode.IsVirtual || !values.ContainsKey(electrode.Id))
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
                // Any numerical issues → safe geometric fallback
                var fallback = new GeometricVirtualElectrodeEstimator();
                return fallback.CompleteElectrodePotentials(electrodes, measuredVoltages, settings, forwardContext);
            }
        }
    }
}
