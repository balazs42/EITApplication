using CommunityToolkit.Mvvm.ComponentModel;
using ServiceLayer;
using TriangleNet.Meshing;
using Utility.Classes.ReconstructionParameters;

using Workspace = Utility.Classes.Application.Workspace;

namespace ElectricalImpedanceTomography.ViewModels
{
    public partial class ReconstructionPageViewModel : BaseReconstructionPageViewModel
    {
        private readonly IReconstructionService _reconstructionService;

        private IMesh? _mesh = (IMesh?)Workspace.GetMesh();

        [ObservableProperty]
        private EITReconstructionParameters reconstructionParameters = Workspace.GetReconstructionParameters();

        [ObservableProperty]
        private int iterationCount = 0;

        [ObservableProperty]
        private double residual = 1.0;

        public ReconstructionPageViewModel(IReconstructionService reconstructionService)
        {
            _reconstructionService = reconstructionService;

            ReconstructionParameters = Workspace.GetReconstructionParameters();
        }


    }
}
