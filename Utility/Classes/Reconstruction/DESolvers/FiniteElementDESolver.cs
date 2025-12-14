using System.Numerics;
using Utility.Classes.Discretizer;
using Utility.Classes.Discretizer.FiniteElementMesh;
using Utility.Classes.Measurement;
using Utility.Classes.ReconstructionParameters;
using Utility.Classes.Solvers.FiniteElementSolver;

namespace Utility.Classes.Reconstruction.DESolvers
{
    public sealed class FiniteElementDESolver : IDifferentialEquationSolver
    {
        private readonly FiniteElementSolver _solver;
        private readonly FEMMesh _mesh;

        public FiniteElementDESolver(FEMMesh mesh, INumericSolver numericSolver, bool useOmpParallelization = false)
        {
            _mesh = mesh;
            _solver = new FiniteElementSolver(mesh, numericSolver, useOmpParallelization);
        }

        /// <summary>
        /// Calculates the arising potential distribution given the mesh's conductivity field, and the applied boundary conditions.
        /// Adjoint solve must be preinitialized appropriate boundary condition.
        /// </summary>
        /// <param name="mesh">The mesh which on we calculate the potential distribution.</param>
        /// <param name="bc">The boundary conditions which should be applied to the calculations.</param>
        /// <returns></returns>
        /// <exception cref="ArgumentException"></exception>
        public PotentialDistribution Solve(IDiscretization discretization, BoundaryCondition bc, Complex[]? adjointSource)
        {
            // Standard forward solve
            if (adjointSource == null)
                return _solver.SolveForward(discretization, bc);
            // Adjoint solve
            else
                return _solver.SolveAdjoint(discretization, bc, adjointSource);
        }
    }
}
