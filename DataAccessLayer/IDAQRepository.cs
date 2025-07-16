using Utility.Classes.Measurement;

namespace DataAccessLayer
{
    public interface IDAQRepository
    {
        public EITMeasurement GetEITMeasurement();
        public void SaveEITMeasurement();
        public void LoadEITMeasurement(DateTime dateTime);
        public void LoadEITMeasurement(int id);
        public void DeleteEITMeasurement(int id);
        public void DeleteEITMEasurement(DateTime dateTime);
    }
}
