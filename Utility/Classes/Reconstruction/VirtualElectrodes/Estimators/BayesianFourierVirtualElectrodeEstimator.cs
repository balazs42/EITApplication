using MathNet.Numerics.LinearAlgebra;
using Utility.Classes.Discretizer;

namespace Utility.Classes.Reconstruction.VirtualElectrodes.Estimators
{
    /// <summary>
    /// Bayesian linear regression over a Fourier basis.
    ///
    /// Prior: θ ~ N(0, τ² I) with variance <see cref="VirtualElectrodeSettings.BayesPriorVariance"/>
    /// Likelihood: y | θ ~ N(Φθ, σ² I) with noise variance <see cref="VirtualElectrodeSettings.BayesNoiseVariance"/>
    /// Posterior mean: θ̂ = (ΦᵀΦ/σ² + I/τ²)^{-1} Φᵀ y / σ²
    /// Prediction: evaluate posterior mean model at all electrode angles.
    /// Falls back to geometric if evidence insufficient or numerics fail.
    /// </summary>
    internal sealed class BayesianFourierVirtualElectrodeEstimator : IVirtualElectrodeEstimator
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
                int kMax = Math.Max(1, settings.FourierOrder);
                while (1 + 2 * kMax > n && kMax > 1)
                    kMax--;

                if (1 + 2 * kMax > n)
                {
                    var fallback = new GeometricVirtualElectrodeEstimator();
                    return fallback.CompleteElectrodePotentials(electrodes, measuredVoltages, settings, forwardContext);
                }

                int parameterCount = 1 + 2 * kMax;
                var phi = Matrix<double>.Build.Dense(n, parameterCount);
                var y = Vector<double>.Build.Dense(n);

                // Build design matrix and target vector
                for (int i = 0; i < n; i++)
                {
                    var (id, angle) = realOrder[i];
                    double measurement = values.TryGetValue(id, out var mv) ? mv : 0.0;
                    y[i] = measurement;
                    phi[i, 0] = 1.0;
                    for (int k = 1; k <= kMax; k++)
                    {
                        phi[i, k] = Math.Cos(k * angle);
                        phi[i, kMax + k] = Math.Sin(k * angle);
                    }
                }

                // Posterior mean solve
                double sigma2 = Math.Max(settings.BayesNoiseVariance, 1e-12);
                double tau2 = Math.Max(settings.BayesPriorVariance, 1e-12);

                var phiTphi = phi.TransposeThisAndMultiply(phi);
                var identity = Matrix<double>.Build.DenseIdentity(parameterCount);
                var lhs = 1.0 / sigma2 * phiTphi + 1.0 / tau2 * identity;
                var rhs = 1.0 / sigma2 * phi.TransposeThisAndMultiply(y);
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
                var fallback = new GeometricVirtualElectrodeEstimator();
                return fallback.CompleteElectrodePotentials(electrodes, measuredVoltages, settings, forwardContext);
            }
        }
    }
}
