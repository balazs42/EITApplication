using Utility.Classes.Measurement;

namespace ServiceLayer
{
    public interface IDAQService
    {
        public EITMeasurements GetEITMeasurement();
    }
}
