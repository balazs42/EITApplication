using System.Numerics;
using Utility.Classes.Measurement;
using Utility.Classes.Discretizer;

namespace Utility.Classes.Solvers
{
    /// <summary>
    /// Defines the interface implemented by forward and adjoint PDE solvers
    /// used during EIT reconstruction.
    /// </summary>
    public interface ISolver
    {
        /// <summary>Solves the forward problem for the provided discretization and boundary data.</summary>
        public PotentialDistribution SolveForward(IDiscretization discretization, BoundaryCondition boundaryCondition);
        /// <summary>Solves the adjoint problem driven by the supplied electrode source.</summary>
        public PotentialDistribution SolveAdjoint(IDiscretization discretization, BoundaryCondition boundaryCondition, Complex[] adjointSource);
    }
}
