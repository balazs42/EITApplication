using Utility.Classes.Reconstruction.VirtualElectrodes;
using Utility.Classes.Reconstruction.VirtualElectrodes.Estimators;

namespace Utility.Classes.Factories
{
    public static class VirtualElectrodeEstimatorFactory
    {
        public static IVirtualElectrodeEstimator Create(VirtualElectrodeSettings settings)
        {
            return settings.Method switch
            {
                VirtualElectrodeMethod.GeometricInterpolation => new GeometricVirtualElectrodeEstimator(),
                VirtualElectrodeMethod.LinearCombination => new LinearCombinationVirtualElectrodeEstimator(),
                VirtualElectrodeMethod.HarrachSensitivityInterpolation => new HarrachVirtualElectrodeEstimator(),
                VirtualElectrodeMethod.NdMapSpectralInterpolation => new NdMapVirtualElectrodeEstimator(),
                VirtualElectrodeMethod.MaximumLikelihoodFourier => new MaximumLikelihoodVirtualElectrodeEstimator(),
                VirtualElectrodeMethod.BayesianFourier => new BayesianFourierVirtualElectrodeEstimator(),
                VirtualElectrodeMethod.GaussianProcessRegression => new GaussianProcessVirtualElectrodeEstimator(),
                _ => new PassthroughVirtualElectrodeEstimator(),
            };
        }
    }
}
