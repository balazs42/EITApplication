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
                .RegisterType<IReconstructionService, AdjointReconstructionService>()
                .RegisterType<IBlockFemReconstructionService, BlockFemReconstructionService>()
                .RegisterType<IReconstructionExportService, ReconstructionExportService>()
                .RegisterType<IMeasurementService, MeasurementService>()
                .RegisterType<ILogger, WorkspaceLogger>();
        }
    }
}
