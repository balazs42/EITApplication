using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ServiceLayer;
using Utility.Classes;
using Utility.Classes.Meshing;
using Utility.Classes.Factories;
using Utility.Classes.Meshing.LatticeBoltzmannMesh;
using Utility.Classes.Measurement;
using Utility.Classes.ReconstructionParameters;

using Workspace = Utility.Classes.Application.Workspace;

namespace ElectricalImpedanceTomography.ViewModels
{
    public partial class LBMReconstructionPageViewModel : BaseReconstructionPageViewModel
    {     
        private readonly IReconstructionService _reconstructionService;

        [ObservableProperty]
        private EITReconstructionParameters reconstructionParameters;

        [ObservableProperty]
        private ReconstructionResult reconstructionResult;

        [ObservableProperty]
        private LBMBoundaryCondition? boundaryCondition;

        [ObservableProperty]
        private int gridSizeNx = 32;

        [ObservableProperty]
        private int gridSizeNy = 32;

        private LBMMesh _mesh;

        public LBMReconstructionPageViewModel(IReconstructionService reconstructionService)
        {
            _reconstructionService = reconstructionService;

            ReconstructionParameters = new EITReconstructionParameters();
            ReconstructionParameters.DifferentialEquationSolver = DifferentialEquationSolver.LatticeBoltzmannMethod;

            Workspace.SetReconstructionParameters(ReconstructionParameters);

            GenerateLbmMesh();
        }

        public void GenerateLbmMesh()
        {
            MeshParameters parameters = new MeshParameters();
            parameters.MeshType = Utility.Classes.Meshing.MeshType.LBM;
            parameters.Nx = GridSizeNx;
            parameters.Ny = GridSizeNy;
            parameters.Radius = 14;
            parameters.ElectrodeCount = 16;

            // Create the underlying data model
            _mesh = (LBMMesh)MeshFactory.Create(parameters);
        }

        public LBMMesh GetMesh() => (LBMMesh)_mesh.DeepCopy();

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
                electrodes[5].IsGround = true;
                electrodes[0].Current = 10.0;
                electrodes[5].Current = -10.0;

                for (int i = 2; i < electrodes.Count; i++)
                {
                    if (!(electrodes[i].IsGround || electrodes[i].IsExcitation))
                        electrodes[i].Potential = 2.0;
                }

                _mesh.SetElectrodes(electrodes);
            }

            var reconstructionParameters = Workspace.GetReconstructionParameters();

            _reconstructionService.InitializeReconstruction(_mesh, reconstructionParameters);

            PotentialDistribution potentialDistribution = _reconstructionService.SolveLbmForward();

            _mesh.SetPotentialDistribution(potentialDistribution);

            ReconstructionResult = new ReconstructionResult((LBMMesh)_mesh,
                                                            potentialDistribution,
                                                            new PotentialDistribution(new()),
                                                            ConductivityDistributionFactory.CreateRandom(_mesh),
                                                            ConductivityDistributionFactory.CreateRandom(_mesh),
                                                            ConductivityDistributionFactory.CreateRandom(_mesh));

            ReconstructionResult.CurrentPotentialDistribution = _mesh.GetPotentialDistribution();

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

            var reconstructionParameters = Workspace.GetReconstructionParameters();

            _reconstructionService.InitializeReconstruction(_mesh, reconstructionParameters);
            
            _reconstructionService.SolveLbmInverse(maxIterCount);


            // TODO: implement
        }

        public ReconstructionResult InverseSolveStep()
        {
            var result = _reconstructionService.SolveLbmInverse(1);
            ReconstructionResult = result;
            OnPropertyChanged(nameof(ReconstructionResult));
            return result;
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

                    Workspace.SetReconstructionParameters(ReconstructionParameters);
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

            Workspace.SetReconstructionParameters(parameters);

            _reconstructionService.InitializeReconstruction(_mesh, parameters);
            var result = await _reconstructionService.GetReconstructionResult();
        }
    }
}
