using Utility.Classes.Measurement;
using System.Numerics;
using Utility.Classes.Solvers.FiniteElementSolver;
using Utility.Classes.Solvers.LatticeBoltzmannSolver;
using Utility.Classes.Meshing.FiniteElementMesh;
using Utility.Classes.Meshing.LatticeBoltzmannMesh;
using Utility.Classes.Solvers.GraphBasedSolver;
using Utility.Classes.Solvers;
using MathNet.Numerics.Financial;

namespace Utility.Classes.ReconstructionParameters
{
    public enum DifferentialEquationSolver
    {
        FiniteElementMethod = 1,
        LatticeBoltzmannMethod = 2,
        GraphBased = 3
    };

    public interface IDifferentialEquationSolver
    {
        /// <summary>
        /// This function can be called to solve the differential equations assocaited to the meshes. Proper 
        /// mesh initialization should be done and boundary conditions in forward case should be set the measured values
        /// in the adjoint case, should be set to the adjoint source.
        /// </summary>
        /// <param name="mesh">Mesh object that will be used to solve the equations.</param>
        /// <param name="boundaryCondition">The specified boundaryConditions</param>
        /// <returns></returns>
        PotentialDistribution Solve(IMesh mesh, BoundaryCondition boundaryCondition, Complex[]? adjointSource);
    }

    public sealed class FiniteElementDESolver : IDifferentialEquationSolver
    {
        private readonly FiniteElementSolver _solver;
        private readonly FEMMesh _mesh;

        public FiniteElementDESolver(FEMMesh mesh, INumericSolver numericSolver)
        {
            _mesh = mesh;
            _solver = new FiniteElementSolver(mesh, numericSolver);
        }

        /// <summary>
        /// Calculates the arising potential distribution given the mesh's conductivity field, and the applied boundary conditions.
        /// Adjoint solve must be preinitialized appropriate boundary condition.
        /// </summary>
        /// <param name="mesh">The mesh which on we calculate the potential distribution.</param>
        /// <param name="bc">The boundary conditions which should be applied to the calculations.</param>
        /// <returns></returns>
        /// <exception cref="ArgumentException"></exception>
        public PotentialDistribution Solve(IMesh mesh, BoundaryCondition bc, Complex[]? adjointSource)
        {
            // Standard forward solve
            if(adjointSource == null)
                return _solver.SolveForward(mesh, bc);
            // Adjoint solve
            else 
                return _solver.SolveAdjoint(mesh, bc, adjointSource);
        }
    }

    public sealed class LatticeBoltzmannDESolver : IDifferentialEquationSolver
    {
        private readonly LatticeBoltzmannSolver _solver;
        private readonly int _maxIterations;
        private readonly double _convergenceThreshold;
        private readonly int _checkInterval;

        public LatticeBoltzmannDESolver(int maxIterations = 20000, double convergenceThreshold = 1e-7, int checkInterval = 100)
        {
            _maxIterations = maxIterations;
            _convergenceThreshold = convergenceThreshold;
            _checkInterval = checkInterval;

            _solver = new LatticeBoltzmannSolver(_maxIterations, _convergenceThreshold, _checkInterval);
        }

        public PotentialDistribution Solve(IMesh mesh, BoundaryCondition bc, Complex[]? adjointSource)
        {
            if (adjointSource == null)
                return _solver.SolveForward(mesh, bc);
            else
                return _solver.SolveAdjoint(mesh, bc, adjointSource);
        }
    }

    public sealed class GraphSolver : IDifferentialEquationSolver
    {
        private readonly INumericSolver _numericSolver;
        private readonly GraphBasedSolver _solver;
        private readonly GraphAssembler _assembler;


        public GraphSolver(INumericSolver numericSolver, GraphBasedSolver solver, GraphAssembler assembler)
        {
            _numericSolver = numericSolver;
            _solver = solver;
            _assembler = assembler;
        }

        /// <summary>
        /// Solves the forward problem to find the potential field φ.
        /// </summary>
        /// TODO: Correct implementation for this
        public PotentialDistribution Solve(IMesh mesh, BoundaryCondition bc, Complex[]? adjointSource)
        {
            if (mesh is FEMMesh femMesh)
            {
                _assembler.Build(femMesh);

                return _solver.SolveForward(femMesh, _numericSolver);
            }
            else
                throw new NotImplementedException("Cannot use graph based solver, the LBM mesh -> graph representation implmenetiation is not yet done!");
        }

        /// <summary>
        /// Solves the adjoint problem to find the adjoint variable μ.
        /// </summary>
        public PotentialDistribution SolveAdjoint(IMesh mesh, BoundaryCondition bc, Complex[] adjointSource)
        {
            if(mesh is FEMMesh femMesh)
            {
                _assembler.Build(femMesh);

                return _solver.SolveAdjoint(femMesh, _numericSolver);
            }
            else
                throw new NotImplementedException("Cannot use graph based solver, the LBM mesh -> graph representation implmenetiation is not yet done!");
        }

        public ConductivityDistribution InverseSolve(IMesh mesh, BoundaryCondition bc, Complex[] ajdointSource)
        {
            if (mesh is FEMMesh femMesh)
            {
                _assembler.Build(femMesh);

                return _solver.Iteration(femMesh, _numericSolver);
            }
            else
                throw new NotImplementedException("Cannot use graph based solver, the LBM mesh -> graph representation implmenetiation is not yet done!");
        }
    }
}
