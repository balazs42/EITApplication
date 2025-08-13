using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ServiceLayer;
using System.Diagnostics.CodeAnalysis;
using Utility.Classes;
using Utility.Classes.Factories;
using Utility.Classes.Meshing.LatticeBoltzmannMesh;
using Utility.Classes.Measurement;
using Utility.Classes.ReconstructionParameters;
namespace ElectricalImpedanceTomography.ViewModels
{
    public partial class LBMReconstructionPageViewModel : BaseViewModel
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

        [ObservableProperty]
        private ReconstructionResult reconstructionResult;

        private LBMMesh _mesh;

        [ObservableProperty]
        private LBMBoundaryCondition? boundaryCondition;

        [ObservableProperty]
        private int gridSizeNx = 32;

        [ObservableProperty]
        private int gridSizeNy = 32;

        public LBMReconstructionPageViewModel(IReconstructionService reconstructionService)
        {
            _reconstructionService = reconstructionService;

            ReconstructionParameters = new EITReconstructionParameters();
            ReconstructionParameters.DifferentialEquationSolver = DifferentialEquationSolver.LatticeBoltzmannMethod;

            GenerateLbmMesh();
        }

        public void GenerateLbmMesh()
        {
            MeshParameters parameters = new MeshParameters();
            parameters.MeshType = Utility.Classes.Meshing.MeshType.LBM;
            parameters.Nx = GridSizeNx;
            parameters.Ny = GridSizeNy;
            parameters.Radius = 10;
            parameters.ElectrodeCount = 16;

            // Create the underlying data model
            _mesh = (LBMMesh)MeshFactory.Create(parameters);
        }

        public LBMMesh GetMesh()
        {
            return (LBMMesh)_mesh.DeepCopy();
        }

        [RelayCommand]
        private void ToggleWallState(object cellInfo)
        {
            if (cellInfo is (int x, int y) && _mesh != null)
            {
                var element = _mesh.GetElementAt(x, y);
                if (element != null)
                    element.IsWall = !element.IsWall;
            }
        }

        [RelayCommand]
        private void ToggleElectrodeState(object cellInfo)
        {
            if (cellInfo is (int x, int y) && _mesh != null)
            {
                var element = _mesh.GetElementAt(x, y);
                if (element != null && !element.IsWall) // Can't place an electrode on a wall
                    element.IsElectrode = !element.IsElectrode;
            }
        }

        public async void OnSolveForwardClicked(object sender, EventArgs e)
        {
            if (BoundaryCondition != null)
            {
                // Use the user‐defined boundary condition
                _mesh.SetElectrodes(BoundaryCondition.GetElectrodes());
            }
            else
            {
                // Fall back to a simple default configuration
                var electrodes = _mesh.GetElectrodes().Cast<LBMElectrode>().ToList();

                foreach (var el in electrodes)
                {
                    el.IsExcitation = false;
                    el.IsGround = false;
                    el.Current = 0.0;
                    el.Potential = 0.0;
                }

                electrodes[0].IsExcitation = true;
                electrodes[1].IsGround = true;
                electrodes[0].Current = 3.0;
                electrodes[1].Current = 1.0;

                for (int i = 2; i < electrodes.Count; i++)
                {
                    if (!(electrodes[i].IsGround || electrodes[i].IsExcitation))
                        electrodes[i].Potential = (i % 2 == 0) ? 2.0 : 1.0;
                }

                _mesh.SetElectrodes(electrodes);
            }

            _reconstructionService.InitializeReconstruction(_mesh, ReconstructionParameters);

            ReconstructionResult = await _reconstructionService.GetReconstructionResult();

            _mesh = (LBMMesh)ReconstructionResult.Mesh.DeepCopy();

            OnPropertyChanged(nameof(ReconstructionResult));
        }

        public void ApplyBoundaryCondition(LBMBoundaryCondition bc)
        {
            BoundaryCondition = bc;
            _mesh.SetElectrodes(bc.GetElectrodes());
        }

        public void OnSolveInverseClicked(object sender, EventArgs e)
        {
            const int maxIterCount = 50;


            _reconstructionService.LBMSolveInverse(maxIterCount);


            // TODO: implement
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

        private async void SetupLBMReconstruction()
        {
            var parameters = new EITReconstructionParameters
            {
                DifferentialEquationSolver = DifferentialEquationSolver.LatticeBoltzmannMethod,
                ErrorMetric = ErrorMetric.L2,
                RegularizationTechnique = RegularizationTechnique.None,
                NumericSolver = NumericSolver.GMRES,
                NumericOptimizer = NumericOptimizer.GradientBased
            };

            _reconstructionService.InitializeReconstruction(_mesh, parameters);
            var result = await _reconstructionService.GetReconstructionResult();
        }
    }
}
