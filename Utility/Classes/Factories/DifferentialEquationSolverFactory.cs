using Utility.Classes.Discretizer;
using Utility.Classes.Discretizer.FiniteElementMesh;
using Utility.Classes.ReconstructionParameters;
using Utility.Classes.Solvers.GraphBasedSolver;

using Workspace = Utility.Classes.Application.Workspace;

namespace Utility.Classes.Factories
{
    /// <summary>
    /// The differential equation factory should be used to create the DE solvers for the forward and inverse problems.
    /// </summary>
    public static class DifferentialEquationSolverFactory
    {
        public static IDifferentialEquationSolver Create(IDiscretization discretization, DifferentialEquationSolver des, INumericSolver numericSolver, bool useOmpParallelization) => des switch
        {
            DifferentialEquationSolver.FEM => CreateFiniteElementSolver((FEMMesh)discretization, numericSolver, useOmpParallelization),
            DifferentialEquationSolver.LBM => CreateLatticeBoltzmannSolver(),
            DifferentialEquationSolver.Graph => CreateGraphBasedSolver((FEMMesh)discretization, numericSolver),
            _ => throw new NotSupportedException()
        };

        private static FiniteElementDESolver CreateFiniteElementSolver(FEMMesh mesh, INumericSolver numericSolver, bool useOmpParallelization)
        {
            var deSolver = new FiniteElementDESolver(mesh, numericSolver, useOmpParallelization);

            Workspace.AddLogMessage("DifferentialEquationSolverFactory", "Created Finite Element solver object.");

            return deSolver;
        }

        private static LatticeBoltzmannDESolver CreateLatticeBoltzmannSolver()
        {
            var deSolver = new LatticeBoltzmannDESolver();

            Workspace.AddLogMessage("DifferentialEquationSolverFactory", "Created Lattice Boltzmann solver object.");

            return deSolver;
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

            Workspace.AddLogMessage("DifferentialEquationSolverFactory", "Created Graph Based solver object.");

            return solver;
        }
    }
}
