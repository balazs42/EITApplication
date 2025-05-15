namespace Utility.Classes.ReconstructionParameters
{
    public enum RegularizationTechnique
    {
        None = 0,
        ZeroOrderTikhonov = 1,
        FirstOrderTikhonov = 2,
        TotalVariation = 3,
        Laplace = 4
    };

    public interface IRegularisation
    {
        double[] Apply(double[] parameterVector);
    }

    public sealed class NoRegularisation : IRegularisation
    {
        public double[] Apply(double[] p) => p;
    }

    public sealed class ZeroOrderTikhonov : IRegularisation
    {
        private readonly double _λ;
        public ZeroOrderTikhonov(double λ = 1e-3) => _λ = λ;
        public double[] Apply(double[] p)
            => p.Select(x => x * _λ).ToArray();
    }

    public sealed class Laplace : IRegularisation
    {
        // TODO: Implement, this is a placeholder
        public double[] Apply(double[] p) => p;
    }

    public sealed class TotalVariaion : IRegularisation
    {
        // TODO: Implement, this is a placeholder
        public double[] Apply(double[] p) => p;
    }

    public static class RegularisationFactory
    {
        public static IRegularisation Create(RegularizationTechnique rt) => rt switch
        {
            RegularizationTechnique.None => new NoRegularisation(),
            RegularizationTechnique.ZeroOrderTikhonov => new ZeroOrderTikhonov(),
            /* … */
            _ => throw new NotSupportedException()
        };
    }
}
