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
        public void SaveEITMeasurement();
        public void LoadEITMeasurement(DateTime dateTime);
        public void LoadEITMeasurement(int id);
        public void DeleteEITMeasurement(int id);
        public void DeleteEITMeasurement(DateTime dateTime);
    }
}

