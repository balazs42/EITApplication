using Utility.Classes.Measurement;

namespace BusinessLayer
{
    public interface IDAQPersistence
    {
        public EITMeasurements GetEITMeasurement();
    }
}
