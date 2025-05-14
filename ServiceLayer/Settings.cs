using Unity;

namespace ServiceLayer
{
    public class Settings
    {
        public static IUnityContainer ApplyContainerRegistration()
        {
            return BusinessLayer.Settings.ApplyContainerRegistration()
                .RegisterType<IDAQService, DAQService>()
                .RegisterType<IReconstructionService, ReconstructionService>();
        }
    }
}
