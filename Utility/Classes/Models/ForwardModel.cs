using Utility.Classes.Measurement;
using Utility.Classes.ReconstructionParameters;

namespace Utility.Classes.Models
{
    public interface IForwardModel
    {
        public PotentialDistribution ForwardSolve();
    }

    public class ForwardModel : IForwardModel
    {
        private readonly INumericSolver _numericSolver;
        private readonly IDifferentialEquationSolver _differentialEquationSolver;
        private readonly IMesh _mesh;
        private readonly ConductivityDistribution _conductivityDistribution;
        private readonly BoundaryConditions _boundaryConditions;

        public ForwardModel(INumericSolver numericSolver, IDifferentialEquationSolver differentialEquationSolver, IMesh mesh, ConductivityDistribution conductivityDistribution, BoundaryConditions boundaryConditions)
        {
            _numericSolver = numericSolver;
            _differentialEquationSolver = differentialEquationSolver;
            _mesh = mesh;
            _conductivityDistribution = conductivityDistribution;
            _boundaryConditions = boundaryConditions;
        }

        public PotentialDistribution ForwardSolve()
        {
            return _differentialEquationSolver.SolveForward(_mesh, _conductivityDistribution, _boundaryConditions);
        }

        public ConductivityDistribution GetConductivityDistribution() => _conductivityDistribution;
        public BoundaryConditions GetBoundaryConditions() => _boundaryConditions;
        public IMesh GetMesh() => _mesh;
    }
}
