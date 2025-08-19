using System.Numerics;
using Utility.Classes.Measurement;

namespace BusinessLayer
{
    public interface IDAQPersistence
    {
        public EITMeasurement GetEITMeasurement();
        public Complex[][] ComputeFourierTransform(EITMeasurement measurement);
        public Complex[][] ComputeDFT(EITMeasurement measurement);
        public double[][] ComputeDCT(EITMeasurement measurement);
        public Complex[][] ComputeFFT(EITMeasurement measurement);
    }
}

