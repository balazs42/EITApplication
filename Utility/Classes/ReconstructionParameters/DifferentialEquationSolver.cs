using Utility.Classes.Meshing;
using Utility.Classes.Solvers;
using Utility.Classes.Measurement;
using System.Numerics;

namespace Utility.Classes.ReconstructionParameters
{
    public enum DifferentialEquationSolver
    {
        FiniteElementMethod = 1,
        LatticeBoltzmannMethod = 2
    };

    public interface IDifferentialEquationSolver
    {
        /// <summary>
        /// Solves the forward problem to find the potential field φ.
        /// </summary>
        PotentialDistribution SolveForward(IMesh mesh, BoundaryCondition bc);

        /// <summary>
        /// Solves the adjoint problem to find the adjoint variable μ.
        /// </summary>
        PotentialDistribution SolveAdjoint(IMesh mesh, BoundaryCondition bc, Complex[] adjointSource);
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
        
        public PotentialDistribution SolveForward(IMesh mesh, BoundaryCondition bc)
        {
            return Solve(mesh, bc as FEMBoundaryCondition ?? throw new InvalidCastException("Cannot cast to FEMBoundary conditions, check calling code!"));
        }

        public PotentialDistribution SolveAdjoint(IMesh mesh, BoundaryCondition bc, Complex[] adjointSource)
        {
            //var homogeneousBC = new BoundaryConditions(bc.Electrodes.Select(e => new Electrode(e.Id, e.VertexIds, 0.0, e.ZContact)), null);
            return Solve(mesh, bc as FEMBoundaryCondition ?? throw new InvalidCastException("Cannot cast to FEMBoundary conditions, check calling code!"));
        }

        /// <summary>
        /// Calculates the arising potential distribution given the mesh's conductivity field, and the applied boundary conditions
        /// </summary>
        /// <param name="mesh">The mesh which on we calculate the potential distribution.</param>
        /// <param name="bc">The boundary conditions which should be applied to the calculations.</param>
        /// <returns></returns>
        /// <exception cref="ArgumentException"></exception>
        private PotentialDistribution Solve(IMesh mesh, FEMBoundaryCondition bc)
        {
            if (mesh is not FEMMesh femMesh)
                throw new ArgumentException("FiniteElementSolver requires an FEMMesh.");

            // It assembles matrices K, M, A, D, grounds the system, and solves.
            // The BuildRhsVector helper will correctly use 'potentialSourceTerm'.
            PotentialDistribution potentialDistribution = _solver.Solve(femMesh, bc.Electrodes);

            return potentialDistribution;
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

            _solver = new LatticeBoltzmannSolver();
        }

        public PotentialDistribution SolveForward(IMesh mesh, BoundaryCondition bc)
        {
            if (mesh is not LBMMesh lbmMesh) 
                throw new ArgumentException("LBM requires an LBMMesh.");

            return _solver.RunForward(lbmMesh, 
                                      bc as LBMBoundaryCondition ?? throw new InvalidCastException("Cannot cast boundary conditions to LBM boundary conditions, check calling code!"), 
                                      _maxIterations, 
                                      _convergenceThreshold, 
                                      _checkInterval);
        }

        public PotentialDistribution SolveAdjoint(IMesh mesh, BoundaryCondition bc, Complex[] adjointSource)
        {
            if (mesh is not LBMMesh lbmMesh) 
                throw new ArgumentException("LBM requires an LBMMesh.");

            throw new NotImplementedException();

            // Run simulation with dummy boundary conditions 
            //return _solver.InverseSolve(lbmMesh,
            //                            bc as LBMBoundaryCondition ?? throw new InvalidCastException("Cannot cast boundary conditions to LBM boundary conditions, check calling code!"),
            //                            _maxIterations, 
            //                            _convergenceThreshold, 
            //                            _checkInterval, 
            //                            adjointSource);
        }
    }
}
