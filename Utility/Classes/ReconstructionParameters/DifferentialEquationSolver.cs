using System.Numerics;
using Utility.Classes.Measurement;
using Utility.Classes.Discretizer;
using Utility.Classes.Discretizer.FiniteElementMesh;
using Utility.Classes.Discretizer.GraphMesh;
using Utility.Classes.Solvers.FiniteElementSolver;
using Utility.Classes.Solvers.GraphBasedSolver;
using Utility.Classes.Solvers.LatticeBoltzmannSolver;

namespace Utility.Classes.ReconstructionParameters
{
    public enum DifferentialEquationSolver
    {
        FEM = 1,
        LBM = 2,
        Graph = 3
    };

    public interface IDifferentialEquationSolver
    {
        /// <summary>
        /// This function can be called to solve the differential equations assocaited to the meshes. Proper 
        /// mesh initialization should be done and boundary conditions in forward case should be set the measured values
        /// in the adjoint case, should be set to the adjoint source.
        /// </summary>
        /// <param name="discretization">Mesh object that will be used to solve the equations.</param>
        /// <param name="boundaryCondition">The specified boundaryConditions</param>
        /// <returns></returns>
        PotentialDistribution Solve(IDiscretization discretization, BoundaryCondition boundaryCondition, Complex[]? adjointSource);
    }

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
            if(adjointSource == null)
                return _solver.SolveForward(discretization, bc);
            // Adjoint solve
            else 
                return _solver.SolveAdjoint(discretization, bc, adjointSource);
        }
    }

    public sealed class LatticeBoltzmannDESolver : IDifferentialEquationSolver
    {
        private readonly LatticeBoltzmannSolver _solver;
        private readonly int _maxIterations;
        private readonly double _convergenceThreshold;
        private readonly int _checkInterval;
        private readonly bool _useCuda;

        public LatticeBoltzmannDESolver(int maxIterations = 2000,
                                        double convergenceThreshold = 1e-7,
                                        int checkInterval = 200,
                                        bool useCudaAcceleration = false)
        {
            _maxIterations = maxIterations;
            _convergenceThreshold = convergenceThreshold;
            _checkInterval = checkInterval;
            _useCuda = useCudaAcceleration;

            _solver = new LatticeBoltzmannSolver(_maxIterations, _convergenceThreshold, _checkInterval, _useCuda);
        }

        public PotentialDistribution Solve(IDiscretization discretization, BoundaryCondition bc, Complex[]? adjointSource)
        {
            if (_useCuda)
            {
                return adjointSource == null
                    ? _solver.CUDASolveForward(discretization, bc)
                    : _solver.CUDASolveAdjoint(discretization, bc, adjointSource);
            }

            return adjointSource == null
                ? _solver.SolveForward(discretization, bc)
                : _solver.SolveAdjoint(discretization, bc, adjointSource);
        }

        public PotentialDistribution CUDASolveForward(IDiscretization discretization, BoundaryCondition bc)
            => _solver.CUDASolveForward(discretization, bc);

        public PotentialDistribution CUDASolveAdjoint(IDiscretization discretization, BoundaryCondition bc, Complex[] adjointSource)
            => _solver.CUDASolveAdjoint(discretization, bc, adjointSource);
    }

    public sealed class GraphSolver : IDifferentialEquationSolver
    {
        private readonly INumericSolver _numericSolver;
        private readonly GraphBasedSolver _solver;
        
        public GraphSolver(INumericSolver numericSolver, GraphBasedSolver solver)
        {
            _numericSolver = numericSolver;
            _solver = solver;            
        }

        /// <summary>
        /// Forward = null adjointSource; Adjoint = non-null adjointSource.
        /// BoundaryCondition 'bc' is expected to have been applied to the mesh
        /// before invoking (same convention as the other DESolvers).
        /// </summary>
        public PotentialDistribution Solve(IDiscretization discretization, BoundaryCondition bc, Complex[]? adjointSource)
        {
            if (discretization is FEMMesh femMesh)
            {
                if (adjointSource == null)
                {
                    // FORWARD: use FEMMesh.ToGraph() -> CEM solve on graph -> FEMMesh.FromGraph()
                    return _solver.SolveForward(femMesh, _numericSolver);
                }
                else
                {
                    // ADJOINT: same CEM operator, RHS injected at electrode equations
                    //return _solver.SolveAdjoint(femMesh, _numericSolver, adjointSource);
                    throw new NotImplementedException();
                }
            }
            else
            {
                throw new NotImplementedException(
                    "Graph-based DE solver currently supports FEM meshes. " +
                    "LBM path can be enabled once LBMGrid graph mapping is integrated here.");
            }
        }

        /// <summary>
        /// Adjoint (graph) solve: use the SAME KKT/CEM operator, but stamp the RHS
        /// on the electrode equations with the provided adjoint source y_ell
        /// (e.g., measurement residuals in volts). U_ground = 0 is enforced
        /// by the same grounding used in the forward system.
        /// </summary>
        public PotentialDistribution SolveAdjoint(FEMMesh mesh, INumericSolver _, Complex[] adjointSource)
        {
            if (adjointSource == null || adjointSource.Length == 0)
                throw new ArgumentException("Adjoint source must be provided (non-empty).");

            // 1) Temporarily set electrode “currents” to the adjoint RHS (volt residuals),
            //    because the bottom block row is where the electrode equations live.
            var electrodes = mesh.GetElectrodes().Cast<FEMElectrode>().ToList();
            var saved = electrodes.Select(e => e.Current).ToArray();
            for (int k = 0; k < electrodes.Count; k++)
            {
                double rhs_k = (k < adjointSource.Length) ? adjointSource[k].Real : 0.0;
                electrodes[k].Current = rhs_k;
            }

            // 2) Solve the same CEM KKT system on the graph
            GraphBasedOperators _ops = _solver.GetOperators();
            Graph _graph = _solver.GetGraph();

            var (phi, U, _) = _ops.SolveCEM(_solver.CurrentEdgeWeights(), mesh);
            _solver.LatestElectrodePotentials = U;

            // 3) Write φ back to the graph and convert to a FEM potential distribution
            for (int i = 0; i < _graph.Vertices.Count; i++)
                _graph.Vertices[i].Potential = phi[i];

            var newMesh = mesh.FromGraph(_graph);

            // 4) Restore original currents (important when iterating)
            for (int k = 0; k < electrodes.Count; k++)
                electrodes[k].Current = saved[k];

            return newMesh!.GetPotentialDistribution();
        }

        public ConductivityDistribution InverseSolve(IDiscretization discretization, BoundaryCondition bc, Complex[] ajdointSource)
        {
            if (discretization is FEMMesh femMesh)
                return _solver.Iteration(femMesh, _numericSolver);
            else
                throw new NotImplementedException("Cannot use graph based solver, the LBM mesh -> graph representation implmenetiation is not yet done!");
        }
    }
}
