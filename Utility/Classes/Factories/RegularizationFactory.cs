using Utility.Classes.Meshing.FiniteElementMesh;
using Utility.Classes.ReconstructionParameters;

namespace Utility.Classes.Factories
{
    public static class RegularisationFactory
    {
        public static IRegularizer Create(RegularizationTechnique rt, IMesh mesh, double lambda = 1e-3) => rt switch
        {
            RegularizationTechnique.None => new NoRegularizer(),
            RegularizationTechnique.ZeroOrderTikhonov => new ZeroOrderTikhonov(((FEMMesh)mesh).DeepCopy().GetConductivityDistribution(), lambda),
            RegularizationTechnique.FirstOrderTikhonov => new FirstOrderTikhonov(lambda),
            RegularizationTechnique.Laplace => new LaplaceRegularizer(lambda),
            RegularizationTechnique.TotalVariation => new TotalVariationRegularizer(lambda),
            _ => throw new NotSupportedException()
        };
    }
}
