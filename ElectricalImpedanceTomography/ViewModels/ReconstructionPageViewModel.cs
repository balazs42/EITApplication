using CommunityToolkit.Mvvm.ComponentModel;
using ServiceLayer;
using Utility.Classes;
using Utility.Classes.Measurement;
using Utility.Classes.Meshing.FiniteElementMesh;
using Utility.Classes.Meshing.LatticeBoltzmannMesh;
using Workspace = Utility.Classes.Application.Workspace;

namespace ElectricalImpedanceTomography.ViewModels
{
    public partial class ReconstructionPageViewModel : BaseReconstructionPageViewModel
    {
        private readonly IReconstructionService _reconstructionService;

        private IMesh? _mesh = Workspace.GetMesh();

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

            // Use global reconstruction parameters stored in the workspace
            ReconstructionParameters = Workspace.GetReconstructionParameters();
        }

        private void UpdateMesh() => _mesh = Workspace.GetMesh();
        private void UpdateReconstructionParameters() => ReconstructionParameters = Workspace.GetReconstructionParameters();

        private void InitializeReconstruction()
        {
            UpdateMesh();
            UpdateReconstructionParameters();

            var mesh = _mesh;
            var reconstructionParameters = ReconstructionParameters;

            _reconstructionService.InitializeReconstruction(mesh, reconstructionParameters);
        }

        public void OnSolveForwardClicked(object sender, EventArgs e)
        {
            InitializeReconstruction();

            if (_mesh is FEMMesh femMesh)
                _reconstructionService.SolveFemForward(femMesh);
            else if (_mesh is LBMMesh lbmMesh)
                _reconstructionService.SolveLbmForward();
        }

        public void OnSolveInverseClicked(object sender, EventArgs e)
        {
            InitializeReconstruction();

            if (_mesh is FEMMesh femMesh)
                _reconstructionService.SolveFemInverse(femMesh, MaxIterationCount, StepSize, RegularizationWeight);
            else if (_mesh is LBMMesh lbmMesh)
                _reconstructionService.SolveLbmInverse(MaxIterationCount);
        }

        private List<double[]> _simulatedMeasurements = [];
        private int _simulatedMeasurementIndex = 0;

        public ReconstructionResult? InverseSolveStep()
        {
            InitializeReconstruction();

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
                return _reconstructionService.SolveLbmInverse(1);

            return null;
        }
    }
}
