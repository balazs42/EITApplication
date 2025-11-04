using Utility.Classes.Discretizer;

namespace Utility.Classes.Reconstruction.VirtualElectrodes.Estimators
{
    public interface IVirtualElectrodeEstimator
    {
        double[] CompleteElectrodePotentials(
            IReadOnlyList<Electrode> electrodes,
            double[] measuredVoltages,
            VirtualElectrodeSettings settings,
            ForwardModelContext? forwardContext = null);
    }
}
