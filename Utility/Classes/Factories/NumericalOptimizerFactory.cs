using Utility.Classes.ReconstructionParameters;

namespace Utility.Classes.Factories
{
    public static class OptimizerFactory
    {
        public static INumericOptimizer Create(NumericOptimizer no) => no switch
        {
            NumericOptimizer.GradientBased => new GradientBasedOptimizer(),
            NumericOptimizer.ParticleSwarm => new ParticleSwarmOptimizer(),
            _ => throw new NotSupportedException()
        };
    }
}
