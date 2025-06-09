namespace Utility.Classes.Measurement
{
    /// <summary>
    /// The boundary conditions define that on which electrodes we specify the 
    /// voltages and currents used during measurement. This can be used for a CEM
    /// type forward simulation step.
    /// </summary>
    public sealed class BoundaryConditions
    {
        public List<Electrode> Electrodes;
        private readonly SixteenElectrodeMeasurement Measurement;

        public BoundaryConditions(List<Electrode> electrodes, SixteenElectrodeMeasurement measurement)
        {
            Electrodes = electrodes;
            Measurement = measurement;

            int gndId = measurement.GNDElectrodeId;
            int excitationId = measurement.ExcitationElectrodeId;

            // Specify the voltages and currents on each electrode
            foreach(Electrode electrode in Electrodes)
            {
                if(electrode.Id == gndId)
                {
                    electrode.IsGround = true;
                    electrode.IsExcitation = true;
                    electrode.IsMeasuring = false;

                    // Set electrode current to -0.5 mA
                    electrode.Current = -0.5;

                    // Set the ground electrodes potential to 0V
                    electrode.Voltage = 0.0;
                }
                else if(electrode.Id == excitationId)
                {
                    electrode.IsGround = false;
                    electrode.IsExcitation = true;
                    electrode.IsMeasuring = false;

                    // Set electrode current to 0.5 mA
                    electrode.Current = 0.5;
                }
                else
                {
                    electrode.IsGround = false;
                    electrode.IsExcitation = false;
                    electrode.IsMeasuring = true;

                    // TODO: set to measurement.Measurement[...]

                    // Currently setting it to 1.0 mV
                    electrode.Voltage = 1.0;
                }
            }
        }        
    }
}
