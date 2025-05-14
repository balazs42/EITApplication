namespace Utility.Classes
{
    public class EITMeasurement
    {
        public double[,] Measurements { get; set; } = new double[16, 16];

        public EITMeasurement(double[,] measurements)
        {
            Measurements = measurements;
        }
    }
}
