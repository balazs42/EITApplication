using Utility.Classes.Reconstruction.NumericOptimizers;
using Utility.Classes.ReconstructionParameters;

using Workspace = Utility.Classes.Application.Workspace;

namespace Utility.Classes.Factories
{
    /// <summary>
    /// The numeric optimizer factory should be used to create the appropriate numeric optimizer
    /// for the inverse solve iteraton gradient descent step.
    /// </summary>
    public static class NumericOptimizerFactory
    {
        // Creates the appropriate numeric optimizer. sigmaPrior should only be used when creating HomotopyOptimizer, else dummy can be passed
        public static INumericOptimizer Create(NumericOptimizer no, ConductivityDistribution? sigmaPrior) => no switch
        {
            NumericOptimizer.GradientBased => CreateGradientBasedOptimizer(),
            NumericOptimizer.Polyak => CreatePolyakHeavyBallOptimizer(),
            NumericOptimizer.ADAM => CreateAdamGradientOptimizer(),
            NumericOptimizer.Nesterov => CreateNesterovAcceleratedGradientOptimizer(),
            NumericOptimizer.GlobalTunneling => CreateGlobalTunnelingDescentOptimizer(),
            NumericOptimizer.HomotopyContinuation => CreateHomotopyContinuationOptimizer(sigmaPrior ?? throw new NullReferenceException()),
            NumericOptimizer.SimulatedAnnealing => CreateSimulatedAnnealingOptimizer(),
            NumericOptimizer.ParticleSwarm => CreateParticleSwarmOptimizer(),
            NumericOptimizer.BFGS => CreateBfgsOptimizer(),
            _ => throw new NotSupportedException()
        };


        private static GradientBasedOptimizer CreateGradientBasedOptimizer() 
        {
            var optimizer = new GradientBasedOptimizer();

            Workspace.AddLogMessage("NumericOptimizerFactory","Created Gradient Based Numeric Optimizer object.");

            return optimizer;
        }
        private static PolyakHeavyBallOptimizer CreatePolyakHeavyBallOptimizer() 
        {
            var optimizer = new PolyakHeavyBallOptimizer();

            Workspace.AddLogMessage("NumericOptimizerFactory","Created Polyak Heavy Ball Numeric Optimizer object.");

            return optimizer;
        }
        private static AdamGradientOptimizer CreateAdamGradientOptimizer() 
        {
            var optimizer = new AdamGradientOptimizer();

            Workspace.AddLogMessage("NumericOptimizerFactory","Created Adam Gradient Numeric Optimizer object.");

            return optimizer;
        }
        private static NesterovAcceleratedGradientOptimizer CreateNesterovAcceleratedGradientOptimizer() 
        {
            var optimizer = new NesterovAcceleratedGradientOptimizer();

            Workspace.AddLogMessage("NumericOptimizerFactory","Created Nesterov Accelerated Gradient Numeric Optimizer object.");

            return optimizer;
        }
        private static GlobalTunnelingDescentOptimizer CreateGlobalTunnelingDescentOptimizer() 
        {
            var optimizer = new GlobalTunnelingDescentOptimizer();

            Workspace.AddLogMessage("NumericOptimizerFactory","Created Global Tunneling Descent Numeric Optimizer object.");

            return optimizer;
        }
        private static HomotopyContinuationOptimizer CreateHomotopyContinuationOptimizer(ConductivityDistribution sigmaPrior)
        {
            var optimizer = new HomotopyContinuationOptimizer(sigmaPrior);

            Workspace.AddLogMessage("NumericOptimizerFactory", "Created Homotopy Continuation Numeric Optimizer object.");

            return optimizer;
        }
        private static SimulatedAnnealingOptimizer CreateSimulatedAnnealingOptimizer() 
        {
            var optimizer = new SimulatedAnnealingOptimizer();

            Workspace.AddLogMessage("NumericOptimizerFactory","Created Simulated Annealing Numeric Optimizer object.");

            return optimizer;
        }
        private static ParticleSwarmOptimizer CreateParticleSwarmOptimizer()
        {
            var optimizer = new ParticleSwarmOptimizer();

            Workspace.AddLogMessage("NumericOptimizerFactory","Created Particle Swarm Numeric Optimizer object.");

            return optimizer;
        }

        private static BfgsOptimizer CreateBfgsOptimizer()
        {
            var optimizer = new BfgsOptimizer();

            Workspace.AddLogMessage("NumericOptimizerFactory","Created BFGS Numeric Optimizer object.");

            return optimizer;
        }
    }
}
