using Utility.Classes.ReconstructionParameters;

namespace Utility.Classes.Factories
{
    public static class RegularisationFactory
    {
        public static IRegularizer Create(RegularizationTechnique rt, IMesh mesh) => rt switch
        {
            RegularizationTechnique.None => new NoRegularizer(),
            RegularizationTechnique.ZeroOrderTikhonov => new ZeroOrderTikhonov(PriorConductivityDistributionGenerator.GenerateHomogeneousDistribution(mesh)),
            RegularizationTechnique.FirstOrderTikhonov => new FirstOrderTikhonov(),
            RegularizationTechnique.Laplace => new LaplaceRegularizer(),
            RegularizationTechnique.TotalVariation => new TotalVariationRegularizer(),
            _ => throw new NotSupportedException()
        };
    }
}
