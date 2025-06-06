using Utility.Classes.ReconstructionParameters;

namespace Utility.Classes.Factories
{
    public static class DifferentialEquationSolverFactory
    {
        public static IDifferentialEquationSolver Create(DifferentialEquationSolver des) => des switch
        {
            DifferentialEquationSolver.FiniteElementMethod => new FiniteElementDESolver(),
            DifferentialEquationSolver.LatticeBoltzmannMethod => new LatticeBoltzmannDESolver(),
            _ => throw new NotSupportedException()
        };
    }
}
