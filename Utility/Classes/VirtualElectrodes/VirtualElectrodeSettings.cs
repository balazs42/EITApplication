using CommunityToolkit.Mvvm.ComponentModel;

namespace Utility.Classes.VirtualElectrodes
{
    public enum VirtualElectrodeMethod
    {
        None = 0,
        GeometricInterpolation = 1,
        LinearCombination = 2,
        HarrachSensitivityInterpolation = 3,
        NdMapSpectralInterpolation = 4,
        MaximumLikelihoodFourier = 5,
        BayesianFourier = 6,
        GaussianProcessRegression = 7
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

        [ObservableProperty]
        private int fourierOrder = 4;

        [ObservableProperty]
        private double mlRegularization = 1e-8;

        [ObservableProperty]
        private double bayesNoiseVariance = 1e-6;

        [ObservableProperty]
        private double bayesPriorVariance = 1.0;

        [ObservableProperty]
        private double gpSignalVariance = 1.0;

        [ObservableProperty]
        private double gpLengthScale = 0.5;

        [ObservableProperty]
        private double gpNoiseVariance = 1e-6;
    }
}
