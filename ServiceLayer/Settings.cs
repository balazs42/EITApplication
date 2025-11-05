using Unity;
using Utility.Logger;

namespace ServiceLayer
{
    public class Settings
    {
        public static IUnityContainer ApplyContainerRegistration()
        {
            return BusinessLayer.Settings.ApplyContainerRegistration()
                .RegisterType<IDAQService, DAQService>()
                .RegisterType<IReconstructionService, ReconstructionService>()
                .RegisterType<IReconstructionExportService, ReconstructionExportService>()
                .RegisterType<IMeasurementService, MeasurementService>()
                .RegisterType<ILogger, WorkspaceLogger>();
        }
    }
}
