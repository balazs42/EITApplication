using Unity;

namespace BusinessLayer
{
    public class Settings
    {
        public static IUnityContainer ApplyContainerRegistration()
        {
            return DataAccessLayer.Settings.ApplyContainerRegistration()
                .RegisterType<IDAQPersistence, DAQPersistence>()
                .RegisterType<IMeasurementPersistence, MeasurementPersistence>()
                .RegisterType<IReconstructionPersistence, ReconstructionPersistence>();
        }
    }
}
