using Utility.Classes.Measurement;

namespace Utility.Classes.Factories
{
    public static class BoundaryConditionFactory
    {
        /// <summary>
        /// Creates a homogeneous boundary condition where all currents are zero.
        /// This is required for the adjoint problem.
        /// </summary>
        public static BoundaryCondition CreateHomogeneous(IMesh mesh)
        {
            List<Electrode> electrodes = mesh.GetElectrodes();

            return new BoundaryCondition(electrodes);
        }
    }
}
