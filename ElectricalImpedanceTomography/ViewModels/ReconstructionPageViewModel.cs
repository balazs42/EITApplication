using CommunityToolkit.Mvvm.ComponentModel;
using ServiceLayer;
using Utility.Classes;
using Utility.Classes.Measurement;
using Utility.Classes.Meshing.FiniteElementMesh;
using Utility.Classes.Meshing.LatticeBoltzmannMesh;
using Utility.Classes.ReconstructionParameters;


using Workspace = Utility.Classes.Application.Workspace;

namespace ElectricalImpedanceTomography.ViewModels
{
    public partial class ReconstructionPageViewModel : BaseReconstructionPageViewModel
    {
        private readonly IReconstructionService _reconstructionService;

        private IMesh? _mesh = Workspace.GetMesh();

        [ObservableProperty]
        private EITReconstructionParameters reconstructionParameters = Workspace.GetReconstructionParameters();

        [ObservableProperty]
        private int iterationCount = 0;

        [ObservableProperty]
        private double residual = 1.0;

        [ObservableProperty]
        private bool adjecentDrivePattern = true;
            
        [ObservableProperty]
        private bool oppositeDrivePattern = false;

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

        private List<double[]> _simulatedMeasurements = [];
        private int _simulatedMeasurementIndex = 0;

        public ReconstructionResult? InverseSolveStep()
        {
            if (_mesh is FEMMesh femMesh)
            {
                if (_simulatedMeasurements.Count == 0)
                    _simulatedMeasurements = _reconstructionService
                        .SimulateFemMeasurements(femMesh, ExcitationCurrentAmplitude);

                var measurement = _simulatedMeasurements[_simulatedMeasurementIndex % _simulatedMeasurements.Count];
                var electrodes = femMesh.GetElectrodes().Cast<FEMElectrode>().ToList();
                var bc = new FEMBoundaryCondition(electrodes);

                var result = _reconstructionService.InverseSolveStepFem(femMesh,
                                                                        measurement,
                                                                        bc,
                                                                        StepSize);
                _simulatedMeasurementIndex++;
                return result;
            }
            else if (_mesh is LBMMesh)
            {
                return _reconstructionService.SolveLbmInverse(1);
            }

            return null;
        }
    }
}
