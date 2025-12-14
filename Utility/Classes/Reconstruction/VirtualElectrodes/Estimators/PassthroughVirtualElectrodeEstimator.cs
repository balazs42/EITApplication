using Utility.Classes.Discretizer;

namespace Utility.Classes.Reconstruction.VirtualElectrodes.Estimators
{
    /// <summary>
    /// Trivial estimator that simply passes through the measured (real) electrode values
    /// and fills virtual electrodes with 0.
    ///
    /// Intended as a baseline or fall-back sanity check.
    /// </summary>
    internal sealed class PassthroughVirtualElectrodeEstimator : IVirtualElectrodeEstimator
    {
        /// <inheritdoc />
        public double[] CompleteElectrodePotentials(IReadOnlyList<Electrode> electrodes, double[] measuredVoltages, VirtualElectrodeSettings settings, ForwardModelContext? forwardContext = null)
        {
            var values = VirtualElectrodeHelpers.BuildMeasuredLookup(electrodes, measuredVoltages);
            return VirtualElectrodeHelpers.MergeVoltages(electrodes, values);
        }
    }
}
