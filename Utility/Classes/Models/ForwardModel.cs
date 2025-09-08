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
        private readonly IDiscretization _discretization;
        private readonly ConductivityDistribution _conductivityDistribution;
        private readonly FEMBoundaryCondition _boundaryCondition;

        public ForwardModel(INumericSolver numericSolver, IDifferentialEquationSolver differentialEquationSolver, IDiscretization discretization, ConductivityDistribution conductivityDistribution, FEMBoundaryCondition boundaryCondition)
        {
            _numericSolver = numericSolver;
            _differentialEquationSolver = differentialEquationSolver;
            _discretization = discretization;
            _conductivityDistribution = conductivityDistribution;
            _boundaryCondition = boundaryCondition;
        }

        public PotentialDistribution ForwardSolve()
        {
            return _differentialEquationSolver.Solve(_discretization, _boundaryCondition, null);
        }

        public ConductivityDistribution GetConductivityDistribution() => _conductivityDistribution;
        public FEMBoundaryCondition GetBoundaryConditions() => _boundaryCondition;
        public IDiscretization GetDiscretization() => _discretization;
    }
}
