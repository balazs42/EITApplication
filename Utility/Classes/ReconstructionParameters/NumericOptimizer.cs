namespace Utility.Classes.ReconstructionParameters
{
    public enum NumericOptimizer
    {
        GradientBased = 1,
        ParticleSwarm = 2
    };

    public interface IOptimizer
    {
        double[] Optimize(Func<double[], double> objective, double[] initialGuess);
    }

    public sealed class GradientBasedOptimizer : IOptimizer
    {
        public double[] Optimize(Func<double[], double> obj, double[] guess)
        {
            // TODO: gradient descent / BFGS / etc.
            return guess;
        }
    }

    public sealed class ParticleSwarmOptimizer : IOptimizer 
    {
        public double[] Optimize(Func<double[], double> obj, double[] guess)
        {
            // TODO: particvel swarm implementation
            return guess;
        }
    }

    public static class OptimizerFactory
    {
        public static IOptimizer Create(NumericOptimizer no) => no switch
        {
            NumericOptimizer.GradientBased => new GradientBasedOptimizer(),
            NumericOptimizer.ParticleSwarm => new ParticleSwarmOptimizer(),
            _ => throw new NotSupportedException()
        };
    }
}
