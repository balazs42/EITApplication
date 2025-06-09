using Utility.Classes.Measurement;

namespace Utility.Classes.Factories
{
    public static class BoundaryConditionFactory
    {
        /// <summary>
        /// Creates a homogeneous boundary condition where all currents are zero.
        /// This is required for the adjoint problem.
        /// </summary>
        public static BoundaryConditions CreateHomogeneous(IMesh mesh)
        {
            var electrodes = mesh.GetElectrodes()
               .Select(e => new Electrode(e.Id, e.MeshId, 0.0, e.ZContact, 0.0) // Set current and voltage to 0
               {
                   IsExcitation = false,
                   IsGround = false,
                   IsMeasuring = true
               })
               .ToList();

            // Create a dummy measurement since it's required by the constructor,
            // though it won't be used for the adjoint solve's BCs.
            var dummyMeasArray = new double[16];
            Array.Fill(dummyMeasArray, double.NaN);

            for (int i = 3; i < dummyMeasArray.Length; i++)
                dummyMeasArray[i] = 0.0;


            var dummySem = new SixteenElectrodeMeasurement(dummyMeasArray, null);

            return new BoundaryConditions(electrodes, dummySem);
        }
    }
}
