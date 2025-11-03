using CommunityToolkit.Mvvm.ComponentModel;

namespace Utility.Classes.VirtualElectrodes
{
    public enum VirtualElectrodeMethod
    {
        None = 0,
        GeometricInterpolation = 1,
        LinearCombination = 2,
        HarrachSensitivityInterpolation = 3,
        NdMapSpectralInterpolation = 4
    }

    public sealed partial class VirtualElectrodeSettings : ObservableObject
    {
        [ObservableProperty]
        private bool useVirtualElectrodes;

        [ObservableProperty]
        private VirtualElectrodeMethod method = VirtualElectrodeMethod.None;

        [ObservableProperty]
        private int virtualElectrodesPerGap = 1;

        [ObservableProperty]
        private double linearCombinationAlpha = 0.5;

        [ObservableProperty]
        private double harrachLambda = 1e-3;

        [ObservableProperty]
        private int ndMaxMode = 8;
    }
}
