using Utility.Classes.Meshing;
using Utility.Classes.Solvers;
using MathNet.Numerics.LinearAlgebra;

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
        PotentialDistribution SolveForward(IMesh mesh, ConductivityDistribution sigma, BoundaryConditions bc);

        /// <summary>
        /// Solves the adjoint problem to find the adjoint variable μ.
        /// </summary>
        PotentialDistribution SolveAdjoint(IMesh mesh, ConductivityDistribution sigma, BoundaryConditions bc, Vector<double> adjointSource);

        /// <summary>
        /// Computes the misfit gradient component (e.g., ∇φ · ∇μ).
        /// </summary>
        ConductivityDistribution ComputeMisfitGradient(IMesh mesh, PotentialDistribution phi, PotentialDistribution mu);
    }

    public sealed class FiniteElementDESolver : IDifferentialEquationSolver
    {
        private readonly FiniteElementSolver _solver;

        public FiniteElementDESolver(INumericSolver numericSolver)
        {
            _solver = new FiniteElementSolver(numericSolver);
        }

        // This method was previously named 'Solve'
        public PotentialDistribution SolveForward(IMesh mesh, ConductivityDistribution sigma, BoundaryConditions bc)
        {
            return _solver.SolveSystem(mesh, sigma, bc, null);
        }

        public PotentialDistribution SolveAdjoint(IMesh mesh, ConductivityDistribution sigma, BoundaryConditions bc, Vector<double> adjointSource)
        {
            var homogeneousBC = new BoundaryConditions(bc.Electrodes.Select(e => new Electrode(e.Id, e.VertexIds, 0.0, e.ZContact)), null);
            return _solver.SolveSystem(mesh, sigma, homogeneousBC, adjointSource);
        }

        public ConductivityDistribution ComputeMisfitGradient(IMesh mesh, PotentialDistribution phi, PotentialDistribution mu)
        {
            return _solver.ComputeGradient(mesh, phi, mu);
        }

        // The private 'Solve' method remains the core engine, now more versatile.
        private PotentialDistribution Solve(IMesh mesh, ConductivityDistribution sigma, BoundaryConditions bc, Vector<double> potentialSourceTerm = null)
        {
            if (mesh is not FEMMesh femMesh)
                throw new ArgumentException("FiniteElementSolver requires an FEMMesh.");

            // It assembles matrices K, M, A, D, grounds the system, and solves.
            // The BuildRhsVector helper will correctly use 'potentialSourceTerm'.
            PotentialDistribution potentialDistribution = _solver.SolveSystem(mesh, sigma, bc, potentialSourceTerm);

            return potentialDistribution;
        }
    }

    public sealed class LatticeBoltzmannDESolver : IDifferentialEquationSolver
    {
        private readonly LatticeBoltzmannSolver _solver;
        private readonly int _iterations;

        public LatticeBoltzmannDESolver(int iterations = 10000)
        {
            _iterations = iterations;
            _solver = new LatticeBoltzmannSolver();
        }

        public PotentialDistribution SolveForward(IMesh mesh, ConductivityDistribution sigma, BoundaryConditions bc)
        {
            if (mesh is not LBMMesh lbmMesh) throw new ArgumentException("LBM requires an LBMMesh.");
            return _solver.RunSimulation(lbmMesh, sigma, bc, _iterations, null);
        }

        public PotentialDistribution SolveAdjoint(IMesh mesh, ConductivityDistribution sigma, BoundaryConditions bc, Vector<double> adjointSource)
        {
            if (mesh is not LBMMesh lbmMesh) throw new ArgumentException("LBM requires an LBMMesh.");
            var homogeneousBC = new BoundaryConditions(new List<Electrode>(), null);
            return _solver.RunSimulation(lbmMesh, sigma, homogeneousBC, _iterations, adjointSource);
        }

        public ConductivityDistribution ComputeMisfitGradient(IMesh mesh, PotentialDistribution phi, PotentialDistribution mu)
        {
            if (mesh is not LBMMesh lbmMesh) throw new ArgumentException("LBM requires an LBMMesh.");
            return _solver.ComputeGradient(lbmMesh, phi, mu);
        }
    }
}
