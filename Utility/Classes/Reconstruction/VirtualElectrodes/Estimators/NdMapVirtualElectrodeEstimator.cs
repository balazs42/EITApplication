using System.Numerics;
using Utility.Classes.Discretizer;

namespace Utility.Classes.Reconstruction.VirtualElectrodes.Estimators
{
    /// <summary>
    /// Low-parameter circular Fourier reconstruction (ND-map like) using discrete Fourier series
    /// computed from the measured real electrodes, then evaluated at all angles.
    ///
    /// Steps:
    /// - Choose number of modes up to <see cref="VirtualElectrodeSettings.NdMaxMode"/> and limited by half the number of real electrodes.
    /// - Compute complex Fourier coefficients from real electrode samples.
    /// - Reconstruct values for any angle using the truncated series; keep measured for real, fill virtual.
    /// </summary>
    internal sealed class NdMapVirtualElectrodeEstimator : IVirtualElectrodeEstimator
    {
        /// <inheritdoc />
        public double[] CompleteElectrodePotentials(IReadOnlyList<Electrode> electrodes, double[] measuredVoltages, VirtualElectrodeSettings settings, ForwardModelContext? forwardContext = null)
        {
            var (realOrder, angles) = VirtualElectrodeHelpers.PrepareOrdering(electrodes, forwardContext);
            if (realOrder.Count == 0)
                return VirtualElectrodeHelpers.MergeVoltages(electrodes, VirtualElectrodeHelpers.BuildMeasuredLookup(electrodes, measuredVoltages));

            // Extract real angles and corresponding measured values in angular order
            var realAngles = realOrder.Select(t => t.Angle).ToArray();
            var realValues = realOrder.Select((entry, idx) => measuredVoltages[Math.Min(idx, measuredVoltages.Length - 1)]).ToArray();
            int realCount = realOrder.Count;

            // Limit max Fourier mode by settings and by sample count (basic safeguard)
            int maxMode = Math.Min(settings.NdMaxMode, Math.Max(1, realCount / 2));

            // Compute complex Fourier coefficients c_n for n = -N..N using DFT-like sum
            var coeffs = new Dictionary<int, Complex>();
            for (int n = -maxMode; n <= maxMode; n++)
            {
                Complex sum = Complex.Zero;
                for (int j = 0; j < realCount; j++)
                {
                    sum += realValues[j] * Complex.Exp(-Complex.ImaginaryOne * n * realAngles[j]);
                }
                coeffs[n] = sum / realCount;
            }

            // Predict for all electrodes by evaluating the truncated series
            var values = VirtualElectrodeHelpers.BuildMeasuredLookup(electrodes, measuredVoltages);
            foreach (var electrode in electrodes)
            {
                double angle = angles[electrode.Id];
                Complex reconstruction = Complex.Zero;
                for (int n = -maxMode; n <= maxMode; n++)
                    reconstruction += coeffs[n] * Complex.Exp(Complex.ImaginaryOne * n * angle);

                if (!values.ContainsKey(electrode.Id) || electrode.IsVirtual)
                    values[electrode.Id] = reconstruction.Real; // series is real for real-valued signals
            }

            return VirtualElectrodeHelpers.MergeVoltages(electrodes, values);
        }
    }
}
