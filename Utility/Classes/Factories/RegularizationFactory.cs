using Utility.Classes.Meshing.FiniteElementMesh;
using Utility.Classes.ReconstructionParameters;

namespace Utility.Classes.Factories
{
    /// <summary>
    /// The regularization factory should be used to create the regulizer applied during the inverse solve step of the reconstruciton.
    /// </summary>
    public static class RegularisationFactory
    {
        public static IRegularizer Create(RegularizationTechnique rt, IMesh mesh, double lambda = 1e-3) => rt switch
        {
            RegularizationTechnique.None => CreateNoRegularizer(),
            RegularizationTechnique.ZeroOrderTikhonov => CreateZeroOrderTikhonovRegulizer(((FEMMesh)mesh).DeepCopy().GetConductivityDistribution()),
            RegularizationTechnique.FirstOrderTikhonov => CreateFirstOrderTikhonovRegulizer(),
            RegularizationTechnique.Laplace => CreateLaplaceRegulizer(),
            RegularizationTechnique.TotalVariation => CreateTotalVariationRegulizer(),
            _ => throw new NotSupportedException()
        };

        private static NoRegularizer CreateNoRegularizer() => new NoRegularizer();
        private static ZeroOrderTikhonov CreateZeroOrderTikhonovRegulizer(ConductivityDistribution conductivityDistribution) => new ZeroOrderTikhonov(conductivityDistribution);
        private static FirstOrderTikhonov CreateFirstOrderTikhonovRegulizer() => new FirstOrderTikhonov();
        private static LaplaceRegularizer CreateLaplaceRegulizer() => new LaplaceRegularizer();
        private static TotalVariationRegularizer CreateTotalVariationRegulizer() => new TotalVariationRegularizer();
    }
}
