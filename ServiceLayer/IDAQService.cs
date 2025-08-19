using System.Numerics;
using Utility.Classes.Measurement;

namespace ServiceLayer
{
    public interface IDAQService
    {
        public EITMeasurement GetEITMeasurement();
        public Complex[][] ComputeFourierTransform(EITMeasurement measurement);
        public Complex[][] ComputeDFT(EITMeasurement measurement);
        public double[][] ComputeDCT(EITMeasurement measurement);
        public Complex[][] ComputeFFT(EITMeasurement measurement);
    }
}

