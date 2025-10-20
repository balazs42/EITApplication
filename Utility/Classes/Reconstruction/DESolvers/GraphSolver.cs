using System.Numerics;
using Utility.Classes.Discretizer;
using Utility.Classes.Discretizer.FiniteElementMesh;
using Utility.Classes.Discretizer.GraphMesh;
using Utility.Classes.Measurement;
using Utility.Classes.ReconstructionParameters;
using Utility.Classes.Solvers.GraphBasedSolver;

namespace Utility.Classes.Reconstruction.DESolvers
{
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
