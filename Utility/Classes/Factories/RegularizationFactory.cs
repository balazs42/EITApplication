using Utility.Classes.ReconstructionParameters;

namespace Utility.Classes.Factories
{
    public static class RegularisationFactory
    {
        public static IRegularisation Create(RegularizationTechnique rt) => rt switch
        {
            RegularizationTechnique.None => new NoRegularisation(),
            RegularizationTechnique.ZeroOrderTikhonov => new ZeroOrderTikhonov(),
            RegularizationTechnique.FirstOrderTikhonov => new FirstOrderTikhonov(),
            RegularizationTechnique.Laplace => new Laplace(),
            RegularizationTechnique.TotalVariation => new TotalVariaion(),
            _ => throw new NotSupportedException()
        };
    }
}
