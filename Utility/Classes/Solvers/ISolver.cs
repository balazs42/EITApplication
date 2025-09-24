using System.Numerics;
using Utility.Classes.Measurement;

namespace Utility.Classes.Solvers
{
    public interface ISolver
    {
        public PotentialDistribution SolveForward(IDiscretization discretization, BoundaryCondition boundaryCondition);
        public PotentialDistribution SolveAdjoint(IDiscretization discretization, BoundaryCondition boundaryCondition, Complex[] adjointSource);
    }
}
