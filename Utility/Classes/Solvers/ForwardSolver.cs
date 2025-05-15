using Utility.Classes.ReconstructionParameters;

namespace Utility.Classes.Solvers
{
    public class ForwardSolver
    {
        private readonly IForwardModel _model;
        public ForwardSolver(DifferentialEquationSolver des) => _model = ForwardModelFactory.Create(des);

        public ForwardSolution Solve(Mesh mesh,
                                     ConductivityDistribution sigma,
                                     BoundaryConditions bc)
            => _model.Solve(mesh, sigma, bc);
    }
}
