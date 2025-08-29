using System.Numerics;
using Utility.Classes.Measurement;

namespace DataAccessLayer
{
    public interface IDAQRepository
    {
        public EITMeasurement GetEITMeasurement();
        public Complex[][] ComputeFourierTransform(EITMeasurement measurement);
        public Complex[][] ComputeDFT(EITMeasurement measurement);
        public double[][] ComputeDCT(EITMeasurement measurement);
        public Complex[][] ComputeFFT(EITMeasurement measurement);

        public void SaveEITMeasurement(EITMeasurement measurement, string name);
        public EITMeasurement LoadEITMeasurement(string name, DateTime savedAt);
        public void DeleteEITMeasurement(string name, DateTime savedAt);

        public bool Connect();
        public bool Disconnect();
    }
}

