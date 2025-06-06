using Utility.Classes.Meshing;
using Utility.Classes.Solvers;

namespace Utility.Classes.ReconstructionParameters
{
    public enum DifferentialEquationSolver
    {
        FiniteElementMethod = 1,
        LatticeBoltzmannMethod = 2
    };

    public interface IDifferentialEquationSolver
    {
        public PotentialDistribution ForwardSolve(IMesh mesh, ConductivityDistribution conductivityDistribution, BoundaryConditions boundaryConditions);
    }

    public sealed class FiniteElementDESolver : IDifferentialEquationSolver
    {
        public PotentialDistribution ForwardSolve(IMesh mesh, ConductivityDistribution conductivityDistribution, BoundaryConditions boundaryConditions)
        {
            if (mesh is not FEMMesh)
                throw new InvalidDataException($"Can not solve DEs, bad mesh type. Nameof(mesh) = {nameof(mesh)}.");

            // Create solver instance
            FiniteElementSolver solver = new FiniteElementSolver();

            // Returning with the calculated potential distribution
            return solver.ForwardSolve(mesh, conductivityDistribution, boundaryConditions).PotentialDistribution;
        }
    }

    public sealed class LatticeBoltzmannDESolver : IDifferentialEquationSolver
    {
        public PotentialDistribution ForwardSolve(IMesh mesh, ConductivityDistribution conductivityDistribution, BoundaryConditions boundaryConditions)
        {
            if (mesh is not LBMMesh grid)
                throw new InvalidDataException($"Can not solve DEs, bad mesh type. Nameof(mesh) = {nameof(mesh)}.");

            // Create solver instance
            LatticeBoltzmannSolver solver = new LatticeBoltzmannSolver();

            // Returning with the calculated potential distribution
            return solver.ForwardSolve(mesh, conductivityDistribution, boundaryConditions).PotentialDistribution;
        }
    }
}
