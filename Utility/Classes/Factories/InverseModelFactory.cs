using Utility.Classes.Discretizer;
using Utility.Classes.Models;
using Utility.Classes.ReconstructionParameters;

namespace Utility.Classes.Factories
{
    public static class InverseModelFactory
    {
        public interface IInverseModel
        {
            public ConductivityDistribution InverseSolve();
        }

        public static InverseModel Create(IDiscretization discretization, INumericOptimizer numericOptimizer, IRegularizer regularizer, IErrorMetric errorMetric, IDifferentialEquationSolver deSolver)
            => new(discretization, numericOptimizer, regularizer, errorMetric, deSolver);
    }
}
