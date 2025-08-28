using CommunityToolkit.Mvvm.ComponentModel;
using ServiceLayer;
using TriangleNet.Meshing;
using Utility.Classes.Meshing.FiniteElementMesh;
using Utility.Classes.Meshing.LatticeBoltzmannMesh;
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

        [ObservableProperty]
        private bool adjecentDrivePattern = true;
            
        [ObservableProperty]
        private bool oppsiteDrivePattern = false;

        public ReconstructionPageViewModel(IReconstructionService reconstructionService)
        {
            _reconstructionService = reconstructionService;

            ReconstructionParameters = Workspace.GetReconstructionParameters();
        }


        public void OnSolveForwardClicked(object sender, EventArgs e)
        {
            if (_mesh is FEMMesh femMesh)
                _reconstructionService.SolveFemForward(femMesh);
            else if (_mesh is LBMMesh lbmMesh)
                _reconstructionService.SolveLbmForward();
        }

        public void OnSolveInverseClicked(object sender, EventArgs e)
        {
            if (_mesh is FEMMesh femMesh)
                _reconstructionService.SolveFemInverse(femMesh, MaxIterationCount, StepSize, RegularizationWeight);
            else if (_mesh is LBMMesh lbmMesh)
                _reconstructionService.SolveLbmInverse(MaxIterationCount);
        }

    }
}
