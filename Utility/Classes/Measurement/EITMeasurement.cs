
namespace Utility.Classes.Measurement
{
    public class EITMeasurements
    {
        public SixteenElectrodeMeasurement[] Measurements { get; set; } = new SixteenElectrodeMeasurement[16];

        public BoundaryConditions BoundaryConditions { get; set; }

        public EITMeasurements(double[,] measurements)
        {
            Measurements = new SixteenElectrodeMeasurement[16];

            for (int i = 0; i < 16; i++)
            {
                double[] currentMeasurement = new double[16];
                for (int j = 0; j < 16; j++)
                    currentMeasurement[j] = measurements[i, j];

                Measurements[i] = new SixteenElectrodeMeasurement(currentMeasurement, null);
            }
        }

        public SixteenElectrodeMeasurement GetMeasurement(int index)
        {
            if (index > 15)
                throw new ArgumentOutOfRangeException(nameof(index), "Cannot reference measurement out of bounds!");

            return Measurements[index];
        }
    }

    // Stores 16 doubles for the measurements, and which electrodes were used for excitation.
    public class SixteenElectrodeMeasurement
    {
        public double[] Measurement { get; set; } = new double[16];
        public DateTime DateTime { get; set; }
        public int GNDElectrodeId { get; set; } = -1;
        public int ExcitationElectrodeId { get; set; } = -1;

        public SixteenElectrodeMeasurement(double[] measurement, DateTime? dateTime)
        {
            if (measurement.Length != 16)
                throw new InvalidDataException("Cannot create measurement instance, if the measurement length is not 16!");

            if (dateTime != null)
                DateTime = (DateTime)dateTime;

            Measurement = measurement;

            List<int> nanPositions = [];
            for (int i = 0; i < Measurement.Length; i++)
                if (double.IsNaN(Measurement[i]))
                    nanPositions.Add(i);

            if (nanPositions.Count > 3 || nanPositions.Count == 0)
                throw new ArgumentOutOfRangeException($"During EITMeasurement initialization, more then 3 NaN values were detected. Check code and HW! Time of the measurement: DateTime = {DateTime.ToString()}");

            // First case is the first measurement where NaN | NaN | Meas1 | Meas2 .... | NaN will show.
            // This means the ground electrode is the first electrode, excitation is the next
            if (nanPositions[0] == 0)
            {
                GNDElectrodeId = nanPositions[0];
                ExcitationElectrodeId = nanPositions[1];
            }
            else
            {
                GNDElectrodeId = nanPositions[1];
                ExcitationElectrodeId = nanPositions[2];
            }
        }

        /// <summary>
        /// Returns the measured differential voltage value between the specified channels. Channels must be adjecent!
        /// </summary>
        /// <param name="channel1"></param>
        /// <param name="channel2"></param>
        /// <returns></returns>
        public double GetMeasurementBetweenChannles(uint channel1, uint channel2)
        {
            if ((channel1 - channel2) != 1 || (channel2 - channel1) != 1 || channel1 == channel2)
                throw new ArgumentOutOfRangeException("Cannot retreive measurement data, where specified channels are not adjecent. Check code!");

            if (channel1 == GNDElectrodeId || channel1 == ExcitationElectrodeId ||
                channel2 == GNDElectrodeId || channel2 == ExcitationElectrodeId)
                throw new InvalidDataException("Cannot retreive measurement data, from excitation channles. Check code!");

            uint chid = (channel1 < channel2) ? channel1 : channel2;

            return Measurement[chid];
        }
    }
}
