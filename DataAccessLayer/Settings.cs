using Unity;

namespace DataAccessLayer
{
    public class Settings
    {
        public static IUnityContainer ApplyContainerRegistration()
        {
            return Utility.Composition.Settings.ApplyContainerRegistration()
                .RegisterType<IDAQRepository, DAQRepository>();
        }
    }
}
