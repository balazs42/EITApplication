namespace Utility.Classes.ReconstructionParameters
{
    public enum NumericOptimizer
    {
        GradientBased = 1,
        ParticleSwarm = 2
    };

    public interface INumericOptimizer
    {
        double[] Optimize(Func<double[], double> objective, double[] initialGuess);
    }

    public sealed class GradientBasedOptimizer : INumericOptimizer
    {
        public double[] Optimize(Func<double[], double> obj, double[] guess)
        {
            // TODO: gradient descent / BFGS / etc.
            return guess;
        }
    }

    public sealed class ParticleSwarmOptimizer : INumericOptimizer
    {
        public double[] Optimize(Func<double[], double> obj, double[] guess)
        {
            // TODO: particvel swarm implementation
            return guess;
        }
    }
}