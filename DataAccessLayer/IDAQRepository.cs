using Utility.Classes.Measurement;

namespace DataAccessLayer
{
    public interface IDAQRepository
    {
        public EITMeasurements GetEITMeasurement();
    }
}
