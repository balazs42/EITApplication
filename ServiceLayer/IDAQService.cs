using System;
using System.Numerics;
using Utility.Classes;
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
        public void SaveEITMeasurement(EITMeasurement measurement, string name);
        public EITMeasurement LoadEITMeasurement(string name, DateTime savedAt);
        public void DeleteEITMeasurement(string name, DateTime savedAt);
        public void SaveMesh(IMesh mesh, string name);
        public IMesh LoadMesh(string name, DateTime savedAt);
    }
}

