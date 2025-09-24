using Utility.Classes.Measurement;
using Utility.Classes.Discretizer.FiniteElementMesh;
using Utility.Classes.Discretizer;

using Workspace = Utility.Classes.Application.Workspace;

namespace Utility.Classes.Factories
{
    public static class BoundaryConditionFactory
    {
        /// <summary>
        /// Creates a homogeneous boundary condition where all currents are zero.
        /// This is required for the adjoint problem.
        /// </summary>
        public static FEMBoundaryCondition CreateHomogeneous(Discretization mesh)
        {
            if (mesh is not FEMMesh femMesh)
                throw new TypeLoadException("Cannot convert mesh to femMesh. Check calling code!");

            List<FEMElectrode> electrodes = [.. femMesh.GetElectrodes().Cast<FEMElectrode>()];

            Workspace.AddLogMessage("BoundaryConditionFactory" ,"Created Homogeneous FEMBoundaryCondition object.");

            return new FEMBoundaryCondition(electrodes);
        }
    }
}
