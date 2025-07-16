using Utility.Classes.Measurement;
using Utility.Classes.Models;
using Utility.Classes.ReconstructionParameters;

namespace Utility.Classes.Factories
{
    public static class ForwardModelFactory
    {
        public static IForwardModel Create(INumericSolver ns, IDifferentialEquationSolver des, IMesh mesh, ConductivityDistribution conductivityDistribution, BoundaryCondition boundaryCondition)
        {
            throw new NotImplementedException();
            //return new ForwardModel(ns, des, mesh, conductivityDistribution, boundaryCondition);
        }
    }
}
