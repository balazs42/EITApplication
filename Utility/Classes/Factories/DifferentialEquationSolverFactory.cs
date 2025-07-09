using Utility.Classes.Meshing;
using Utility.Classes.ReconstructionParameters;

namespace Utility.Classes.Factories
{
    public static class DifferentialEquationSolverFactory
    {
        public static IDifferentialEquationSolver Create(IMesh mesh, DifferentialEquationSolver des, INumericSolver numericSolver) => des switch
        {
            DifferentialEquationSolver.FiniteElementMethod => new FiniteElementDESolver((FEMMesh)mesh, numericSolver),
            DifferentialEquationSolver.LatticeBoltzmannMethod => new LatticeBoltzmannDESolver(),
            _ => throw new NotSupportedException()
        };
    }
}
