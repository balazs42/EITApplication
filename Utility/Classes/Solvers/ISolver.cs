using System.Numerics;
using Utility.Classes.Measurement;

namespace Utility.Classes.Solvers
{
    public interface ISolver
    {
        public PotentialDistribution SolveForward(IMesh mesh, BoundaryCondition boundaryCondition);
        public PotentialDistribution SolveAdjoint(IMesh mesh, BoundaryCondition boundaryCondition, Complex[] adjointSource);
    }
}
