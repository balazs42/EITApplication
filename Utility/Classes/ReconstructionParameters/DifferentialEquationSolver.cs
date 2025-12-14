using System.Numerics;
using Utility.Classes.Measurement;
using Utility.Classes.Discretizer;

namespace Utility.Classes.ReconstructionParameters
{
    public enum DifferentialEquationSolver
    {
        FEM = 1,
        LBM = 2,
        Graph = 3
    };

    public interface IDifferentialEquationSolver
    {
        /// <summary>
        /// This function can be called to solve the differential equations assocaited to the meshes. Proper 
        /// mesh initialization should be done and boundary conditions in forward case should be set the measured values
        /// in the adjoint case, should be set to the adjoint source.
        /// </summary>
        /// <param name="discretization">Mesh object that will be used to solve the equations.</param>
        /// <param name="boundaryCondition">The specified boundaryConditions</param>
        /// <returns></returns>
        PotentialDistribution Solve(IDiscretization discretization, BoundaryCondition boundaryCondition, Complex[]? adjointSource);
    }
}
