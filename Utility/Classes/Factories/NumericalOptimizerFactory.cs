using Utility.Classes.ReconstructionParameters;

namespace Utility.Classes.Factories
{
    public static class NumericOptimizerFactory
    {
        // Creates the appropriate numeric optimizer. sigmaPrior should only be used when creating HomotopyOptimizer, else dummy can be passed
        public static INumericOptimizer Create(NumericOptimizer no, ConductivityDistribution sigmaPrior) => no switch
        {
            NumericOptimizer.GradientBased => new GradientBasedOptimizer(),
            NumericOptimizer.PolyakHeavyBall => new PolyakHeavyBallOptimizer(),
            NumericOptimizer.ADAM => new AdamGradientOptimizer(),
            NumericOptimizer.NesterovAcceleratedGradient => new NesterovAcceleratedGradientOptimizer(),
            NumericOptimizer.GlobalTunnelingDescent => new GlobalTunnelingDescentOptimizer(),
            NumericOptimizer.HomotopyContinuation => new HomotopyContinuationOptimizer(sigmaPrior),
            NumericOptimizer.SimulatedAnnealing => new SimulatedAnnealingOptimizer(),
            NumericOptimizer.ParticleSwarm => new ParticleSwarmOptimizer(),
            _ => throw new NotSupportedException()
        };
    }
}
