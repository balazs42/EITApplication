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
        private readonly BoundaryCondition _boundaryCondition;

        public ForwardModel(INumericSolver numericSolver, IDifferentialEquationSolver differentialEquationSolver, IMesh mesh, ConductivityDistribution conductivityDistribution, BoundaryCondition boundaryCondition)
        {
            _numericSolver = numericSolver;
            _differentialEquationSolver = differentialEquationSolver;
            _mesh = mesh;
            _conductivityDistribution = conductivityDistribution;
            _boundaryCondition = boundaryCondition;
        }

        public PotentialDistribution ForwardSolve()
        {
            return _differentialEquationSolver.SolveForward(_mesh, _boundaryCondition);
        }

        public ConductivityDistribution GetConductivityDistribution() => _conductivityDistribution;
        public BoundaryCondition GetBoundaryConditions() => _boundaryCondition;
        public IMesh GetMesh() => _mesh;
    }
}
