using CommunityToolkit.Mvvm.ComponentModel;
using ServiceLayer;
using Utility.Classes;
using Utility.Classes.Discretizer;
using Utility.Classes.Discretizer.FiniteElementMesh;
using Utility.Classes.Discretizer.LatticeBoltzmannGrid;
using System.Collections.ObjectModel;

using Workspace = Utility.Classes.Application.Workspace;

namespace ElectricalImpedanceTomography.ViewModels
{
    public partial class ReconstructionPageViewModel : BaseReconstructionPageViewModel
    {
        private readonly IReconstructionService _reconstructionService;

        private IDiscretization? _discretization = Workspace.GetDiscretization();
        private IDiscretization? _initializedDiscretization;

        [ObservableProperty]
        private int iterationCount = 0;

        [ObservableProperty]
        private double residual = 1.0;

        [ObservableProperty]
        private bool adjecentDrivePattern = true;

        [ObservableProperty]
        private bool oppositeDrivePattern = false;

        [ObservableProperty]
        private string name = string.Empty;

        [ObservableProperty]
        private string reconstructionSearchText = string.Empty;

        public ObservableCollection<ReconstructionInfo> AvailableReconstructions { get; } = [];
        public ObservableCollection<ReconstructionInfo> FilteredReconstructions { get; } = [];

        public event EventHandler<ReconstructionResult>? ReconstructionUpdated;
        public event EventHandler<ReconstructionFrame>? ReconstructionFrameUpdated;

        public ReconstructionPageViewModel(IReconstructionService reconstructionService)
        {
            _reconstructionService = reconstructionService;

            // Use global reconstruction parameters stored in the workspace
            ReconstructionParameters = Workspace.GetReconstructionParameters();

            _reconstructionService.ReconstructionUpdated += OnServiceReconstructionUpdated;
            _reconstructionService.ReconstructionFrameUpdated += OnServiceFrameUpdated;
        }

        partial void OnReconstructionSearchTextChanged(string value) => ApplyReconstructionFilter();
        private void UpdateMesh() => _discretization = Workspace.GetDiscretization();
        private void UpdateReconstructionParameters() => ReconstructionParameters = Workspace.GetReconstructionParameters();

        private void InitializeReconstruction(bool force = false)
        {
            UpdateMesh();
            UpdateReconstructionParameters();

            var mesh = _discretization;
            var reconstructionParameters = ReconstructionParameters;

            if (mesh == null)
                throw new NullReferenceException("Mesh was null during reconstruction initialization, check calling code!");

            if (force || _initializedDiscretization != mesh)
            {
                _reconstructionService.InitializeReconstruction(mesh, reconstructionParameters, true);
                _initializedDiscretization = mesh;

                IterationCount = 0;
            }
        }

        public bool CheckReconstructionMethodAgainstMesh()
        {
            if(_discretization is FEMMesh)
                if (ReconstructionParameters.DifferentialEquationSolver != Utility.Classes.ReconstructionParameters.DifferentialEquationSolver.FiniteElementMethod)
                    return false;
            else if(_discretization is LBMGrid)
                if (ReconstructionParameters.DifferentialEquationSolver != Utility.Classes.ReconstructionParameters.DifferentialEquationSolver.LatticeBoltzmannMethod)
                    return false;

            return true;
        }

        public async void OnSolveForwardClicked(object sender, EventArgs e)
        {
            InitializeReconstruction(force: true);

            if (_discretization is FEMMesh)
                await Task.Run(() => _reconstructionService.ForwardSolveStepFem());
            else if (_discretization is LBMGrid)
                await Task.Run(() => _reconstructionService.ForwardSolveStepLbm());
        }

        public async void OnSolveInverseClicked(object sender, EventArgs e)
        {
            InitializeReconstruction(force: true);

            if (_discretization is FEMMesh)
                await Task.Run(() => _reconstructionService.InverseSolveFem(MaxIterationCount, StepSize, RegularizationWeight, ExcitationCurrentAmplitude));
            else if (_discretization is LBMGrid)
                await Task.Run(() => _reconstructionService.InverseSolveLbm(MaxIterationCount, StepSize, RegularizationWeight, ExcitationCurrentAmplitude));
        }

        private double CalculateResidual(ConductivityDistribution reconstructed, ConductivityDistribution original)
        {
            double sum = 0.0;
            foreach (var kv in reconstructed.Conductivities)
            {
                original.Conductivities.TryGetValue(kv.Key, out double origVal);
                double diff = kv.Value - origVal;
                sum += diff * diff;
            }
            return Math.Sqrt(sum);
        }

        public void StartBackgroundReconstruction()
        {
            InitializeReconstruction(force: true);
            _reconstructionService.StartBackgroundReconstruction(MaxIterationCount, StepSize, RegularizationWeight, ExcitationCurrentAmplitude);
        }

        public void PauseReconstruction() => _reconstructionService.PauseBackgroundReconstruction();
        public void ResumeReconstruction() => _reconstructionService.ResumeBackgroundReconstruction();

        public void StopReconstruction()
        {
            _reconstructionService.StopBackgroundReconstruction();
            _initializedDiscretization = null;
        }

        public Task<ReconstructionFrame?> StepReconstructionAsync()
        {
            InitializeReconstruction();
            return _reconstructionService.StepReconstructionAsync();
        }

        public Task<ReconstructionResult?> RunFullReconstructionCycleAsync()
        {
            InitializeReconstruction();
            return _reconstructionService.RunFullReconstructionCycleAsync(StepSize,
                                                                          RegularizationWeight,
                                                                          ExcitationCurrentAmplitude);
        }

        private void OnServiceReconstructionUpdated(object? sender, ReconstructionResult result)
        {
            IterationCount++;
            Residual = CalculateResidual(result.ReconstructedConductivityDistribution,
                                         result.OriginalConductivityDistribution);
            ReconstructionUpdated?.Invoke(this, result);
        }

        private void OnServiceFrameUpdated(object? sender, ReconstructionFrame frame)
            => ReconstructionFrameUpdated?.Invoke(this, frame);

        public void SaveReconstruction()
        {
            var results = Workspace.GetReconstructionResults();
            if (results.Count == 0 || string.IsNullOrWhiteSpace(Name))
                return;
            _reconstructionService.SaveReconstruction(results, Name, ReconstructionParameters);
            LoadAvailableReconstructions();
        }

        public void LoadReconstruction(string filePath)
        {
            _reconstructionService.LoadReconstruction(filePath);
        }

        public void LoadAvailableReconstructions()
        {
            AvailableReconstructions.Clear();
            foreach (var r in _reconstructionService.GetReconstructions())
                AvailableReconstructions.Add(r);
            ApplyReconstructionFilter();
        }

        private void ApplyReconstructionFilter()
        {
            FilteredReconstructions.Clear();
            foreach (var r in AvailableReconstructions.Where(r =>
                         string.IsNullOrWhiteSpace(ReconstructionSearchText) ||
                         r.Name.Contains(ReconstructionSearchText, StringComparison.OrdinalIgnoreCase)))
                FilteredReconstructions.Add(r);
        }
    }
}