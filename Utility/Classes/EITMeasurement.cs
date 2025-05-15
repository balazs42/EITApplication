namespace Utility.Classes
{
    public class EITMeasurement
    {
        public double[,] Measurements { get; set; } = new double[16, 16];
        public BoundaryConditions BoundaryConditions { get; set; }

        public EITMeasurement(double[,] measurements)
        {
            Measurements = measurements;
        }

        public double[] GetMeasurement(int index)
        {
            if (index > 16)
                throw new ArgumentOutOfRangeException(nameof(index), "Cannot reference measurement out of bounds!");

            double[] measurement = new double[16];

            for(int i = 0; i < 16; i++)
                measurement[i] = Measurements[index, i];

            return measurement;
        }
    }
}
