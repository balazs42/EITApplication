using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ServiceLayer;
using System.Collections.ObjectModel;
using Utility.Classes;
using Utility.Classes.Meshing;
using Utility.Classes.ReconstructionParameters;

namespace ElectricalImpedanceTomography.ViewModels
{
    public partial class ReconstructionPageViewModel : BaseViewModel
    {
        private readonly IReconstructionService _reconstructionService;

        public IEnumerable<DifferentialEquationSolver> DifferentialEquationSolverOptions
           => Enum.GetValues(typeof(DifferentialEquationSolver))
                  .Cast<DifferentialEquationSolver>();

        public IEnumerable<RegularizationTechnique> RegularizationTechniqueOptions
            => Enum.GetValues(typeof(RegularizationTechnique))
                   .Cast<RegularizationTechnique>();

        public IEnumerable<ErrorMetric> ErrorMetricOptions
            => Enum.GetValues(typeof(ErrorMetric))
                   .Cast<ErrorMetric>();

        public IEnumerable<NumericSolver> NumericSolverOptions
            => Enum.GetValues(typeof(NumericSolver))
                   .Cast<NumericSolver>();

        public IEnumerable<NumericOptimizer> NumericOptimizerOptions
            => Enum.GetValues(typeof(NumericOptimizer))
                   .Cast<NumericOptimizer>();


        [ObservableProperty]
        private EITReconstructionParameters reconstructionParameters;

        public LBMMesh? LbmMesh { get; private set; } = null;

        // TODO: Implementation of this mesh type
        public FEMMesh? FemMesh { get; private set; } = null;

        [ObservableProperty]
        private int gridSizeNx = 15;

        [ObservableProperty]
        private int gridSizeNy = 15;

        public ReconstructionPageViewModel(IReconstructionService reconstructionService)
        {
            _reconstructionService = reconstructionService;

            ReconstructionParameters = new EITReconstructionParameters();

            GenerateLbmMesh();
        }

        private void GenerateLbmMesh()
        {
            // Create the underlying data model
            LbmMesh = new LBMMesh(GridSizeNx, GridSizeNy);
        }

        [RelayCommand]
        private void ToggleWallState(object cellInfo)
        {
            if (cellInfo is (int x, int y))
            {
                var element = LbmMesh.GetElementAt(x, y);
                if (element != null)
                    element.IsWall = !element.IsWall;
            }
        }

        [RelayCommand]
        private void ToggleElectrodeState(object cellInfo)
        {
            if (cellInfo is (int x, int y))
            {
                var element = LbmMesh.GetElementAt(x, y);
                if (element != null && !element.IsWall) // Can't place an electrode on a wall
                    element.IsElectrode = !element.IsElectrode;
            }
        }


        public async void OnStartReconstructionClicked(object sender, EventArgs e)
        {
            IMesh mesh = GetCurrentMesh();
            _reconstructionService.InitializeReconstruction(mesh, ReconstructionParameters);

            var result = await _reconstructionService.GetReconstructionResult();
        }

        public void OnReconstructionParametersChanged(object sender, EventArgs e)
        {
            if(sender is Picker picker)
            {                
                if(picker.BindingContext is EITReconstructionParameters reconstructionParameter)
                {
                    DifferentialEquationSolver differentialEquationSolver = reconstructionParameter.DifferentialEquationSolver;
                    RegularizationTechnique regularizationTechnique = reconstructionParameter.RegularizationTechnique;
                    ErrorMetric errorMetric = reconstructionParameter.ErrorMetric;
                    NumericSolver numericSolver = reconstructionParameter.NumericSolver;
                    NumericOptimizer numericOptimizer = reconstructionParameter.NumericOptimizer;

                    ReconstructionParameters = new EITReconstructionParameters(differentialEquationSolver,
                                                                               regularizationTechnique, 
                                                                               errorMetric, 
                                                                               numericSolver, 
                                                                               numericOptimizer);
                }
            }
        }

        private IMesh GetCurrentMesh()
        {
            IMesh? mesh = null;

            if (FemMesh != null)
                mesh = FemMesh;
            else if (LbmMesh != null)
                mesh = LbmMesh;

            if (mesh == null)
                throw new NullReferenceException("Cannot initialize reconstruciton, with LbmMesh and FemMesh being null, check code!");

            return mesh;
        }

        private async void SetupLBMReconstruction()
        {
            IMesh mesh = GetCurrentMesh();

            var parameters = new EITReconstructionParameters
            {
                DifferentialEquationSolver = DifferentialEquationSolver.LatticeBoltzmannMethod,
                ErrorMetric = ErrorMetric.L2,
                RegularizationTechnique = RegularizationTechnique.None,
                NumericSolver = NumericSolver.GMRES,
                NumericOptimizer = NumericOptimizer.GradientBased
            };

            _reconstructionService.InitializeReconstruction(mesh, parameters);
            var result = await _reconstructionService.GetReconstructionResult();
        }
    }
}
