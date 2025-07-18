using Utility.Classes.ReconstructionParameters;

namespace Utility.Classes.Factories
{
    /// <summary>
    /// The numeric optimizer factory should be used to create the appropriate numeric optimizer
    /// for the inverse solve iteraton gradient descent step.
    /// </summary>
    public static class NumericOptimizerFactory
    {
        // Creates the appropriate numeric optimizer. sigmaPrior should only be used when creating HomotopyOptimizer, else dummy can be passed
        public static INumericOptimizer Create(NumericOptimizer no, ConductivityDistribution sigmaPrior) => no switch
        {
            NumericOptimizer.GradientBased => CreateGradienBasedOptimizer(),
            NumericOptimizer.PolyakHeavyBall => CreatePolyakHeavyBallOptimizer(),
            NumericOptimizer.ADAM => CreateAdamGradientOptimizer(),
            NumericOptimizer.NesterovAcceleratedGradient => CreateNesterovAcceleratedGradientOptimizer(),
            NumericOptimizer.GlobalTunnelingDescent => CreateGlobalTunnelingDescentOptimizer(),
            NumericOptimizer.HomotopyContinuation => CreateHomotopyContinuationOptimizer(sigmaPrior),
            NumericOptimizer.SimulatedAnnealing => CreateSimulatedAnnealingOptimizer(),
            NumericOptimizer.ParticleSwarm => CreateParticleSwarmOptimizer(),
            _ => throw new NotSupportedException()
        };


        private static GradientBasedOptimizer CreateGradienBasedOptimizer() => new GradientBasedOptimizer();
        private static PolyakHeavyBallOptimizer CreatePolyakHeavyBallOptimizer() => new PolyakHeavyBallOptimizer();
        private static AdamGradientOptimizer CreateAdamGradientOptimizer() => new AdamGradientOptimizer();
        private static NesterovAcceleratedGradientOptimizer CreateNesterovAcceleratedGradientOptimizer() => new NesterovAcceleratedGradientOptimizer();
        private static GlobalTunnelingDescentOptimizer CreateGlobalTunnelingDescentOptimizer() => new GlobalTunnelingDescentOptimizer();
        private static HomotopyContinuationOptimizer CreateHomotopyContinuationOptimizer(ConductivityDistribution sigmaPrior) => new HomotopyContinuationOptimizer(sigmaPrior);
        private static SimulatedAnnealingOptimizer CreateSimulatedAnnealingOptimizer() => new SimulatedAnnealingOptimizer();
        private static ParticleSwarmOptimizer CreateParticleSwarmOptimizer() => new ParticleSwarmOptimizer();


    }
}
