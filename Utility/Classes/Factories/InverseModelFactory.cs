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

        public static InverseModel Create(IMesh mesh, INumericOptimizer numericOptimizer, IRegularizer regularizer, IErrorMetric errorMetric, IDifferentialEquationSolver deSolver)
        {
            return new InverseModel(mesh, numericOptimizer, regularizer, errorMetric, deSolver);
        }
    }
}
