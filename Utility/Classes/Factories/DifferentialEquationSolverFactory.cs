using Utility.Classes.Meshing.FiniteElementMesh;
using Utility.Classes.ReconstructionParameters;
using Utility.Classes.Solvers.GraphBasedSolver;

namespace Utility.Classes.Factories
{
    /// <summary>
    /// The differential equation factory should be used to create the DE solvers for the forward and inverse problems.
    /// </summary>
    public static class DifferentialEquationSolverFactory
    {
        public static IDifferentialEquationSolver Create(IMesh mesh, DifferentialEquationSolver des, INumericSolver numericSolver) => des switch
        {
            DifferentialEquationSolver.FiniteElementMethod => CreateFiniteElementSolver((FEMMesh)mesh, numericSolver),
            DifferentialEquationSolver.LatticeBoltzmannMethod => CreateLatticeBoltzmannSolver(),
            DifferentialEquationSolver.GraphBased => CreateGraphBasedSolver((FEMMesh)mesh, numericSolver),
            _ => throw new NotSupportedException()
        };

        private static FiniteElementDESolver CreateFiniteElementSolver(FEMMesh mesh, INumericSolver numericSolver)
        {
            return new FiniteElementDESolver(mesh, numericSolver);
        }

        private static LatticeBoltzmannDESolver CreateLatticeBoltzmannSolver()
        {
            return new LatticeBoltzmannDESolver();
        }

        private static GraphSolver CreateGraphBasedSolver(FEMMesh mesh, INumericSolver numericSolver)
        {
            double lambdaW = 1.0;
            double lambdaAlpha = 1.0;
            double stepW = 1.0;
            double stepAlpha = 1.0;
            double epsilon = 1e-3;

            GraphBasedSolver graphBasedSolver = new(mesh, numericSolver, lambdaW, lambdaAlpha, stepW, stepAlpha, epsilon);

            GraphSolver solver = new(numericSolver, graphBasedSolver);

            return solver;
        }
    }
}
