using CommunityToolkit.Mvvm.ComponentModel;
using System.ComponentModel;
using Utility.Classes.Reconstruction;
using Utility.Classes.ReconstructionParameters;
using Utility.Classes.Factories;

using Workspace = Utility.Classes.Application.Workspace;

namespace ElectricalImpedanceTomography.ViewModels
{
    public partial class BaseReconstructionPageViewModel : BaseViewModel
    {
        private EITReconstructionParameters _currentParameters;

        private static readonly DifferentialEquationSolver[] DifferentialEquationSolverValues = Enum.GetValues<DifferentialEquationSolver>();
        public IEnumerable<DifferentialEquationSolver> DifferentialEquationSolverOptions => DifferentialEquationSolverValues;

        private static readonly RegularizationTechnique[] RegularizationTechniqueValues = Enum.GetValues<RegularizationTechnique>();
        public IEnumerable<RegularizationTechnique> RegularizationTechniqueOptions => RegularizationTechniqueValues;

        private static readonly ErrorMetric[] ErrorMetricValues = Enum.GetValues<ErrorMetric>();
        public IEnumerable<ErrorMetric> ErrorMetricOptions => ErrorMetricValues;

        private static readonly NumericSolver[] NumericSolverValues = Enum.GetValues<NumericSolver>();
        public IEnumerable<NumericSolver> NumericSolverOptions => NumericSolverValues;

        private static readonly NumericOptimizer[] NumericOptimizerValues = Enum.GetValues<NumericOptimizer>();
        public IEnumerable<NumericOptimizer> NumericOptimizerOptions => NumericOptimizerValues;

        private static readonly InitialDistributionTypes[] InitialDistributionTypeValues = Enum.GetValues<InitialDistributionTypes>();
        public IEnumerable<InitialDistributionTypes> InitialDistributionOptions => InitialDistributionTypeValues;

        private static readonly MeasurementNoiseType[] MeasurementNoiseTypeValues = Enum.GetValues<MeasurementNoiseType>();
        public IEnumerable<MeasurementNoiseType> MeasurementNoiseTypeOptions => MeasurementNoiseTypeValues;

        [ObservableProperty]
        private EITReconstructionParameters reconstructionParameters = Workspace.GetReconstructionParameters();

        public bool IsNoiseAmplitudeEnabled => ReconstructionParameters.MeasurementNoiseType != MeasurementNoiseType.None;

        public BaseReconstructionPageViewModel()
        {
            _currentParameters = ReconstructionParameters;
            _currentParameters.PropertyChanged += OnReconstructionParametersPropertyChanged;
            OnPropertyChanged(nameof(IsNoiseAmplitudeEnabled));
            Workspace.ConductivityMinimumBound = _currentParameters.ConductivityMinimumBound;
            Workspace.ConductivityMaximumBound = _currentParameters.ConductivityMaximumBound;
            ConductivityClipper.UpdateBounds(_currentParameters.ConductivityMinimumBound,
                                             _currentParameters.ConductivityMaximumBound);
        }

        partial void OnReconstructionParametersChanged(EITReconstructionParameters value)
        {
            if (_currentParameters != null)
                _currentParameters.PropertyChanged -= OnReconstructionParametersPropertyChanged;

            Workspace.SetReconstructionParameters(value);

            _currentParameters = value;
            _currentParameters.PropertyChanged += OnReconstructionParametersPropertyChanged;
            OnPropertyChanged(nameof(IsNoiseAmplitudeEnabled));
        }

        private void OnReconstructionParametersPropertyChanged(object? sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName == nameof(EITReconstructionParameters.MeasurementNoiseType))
                OnPropertyChanged(nameof(IsNoiseAmplitudeEnabled));

            if (e.PropertyName == nameof(EITReconstructionParameters.ConductivityMinimumBound)
                || e.PropertyName == nameof(EITReconstructionParameters.ConductivityMaximumBound))
            {
                var parameters = ReconstructionParameters;
                if (parameters != null)
                {
                    Workspace.ConductivityMinimumBound = parameters.ConductivityMinimumBound;
                    Workspace.ConductivityMaximumBound = parameters.ConductivityMaximumBound;
                    ConductivityClipper.UpdateBounds(parameters.ConductivityMinimumBound,
                                                     parameters.ConductivityMaximumBound);
                }
            }
        }

        [ObservableProperty]
        private int layers = 2;

        [ObservableProperty]
        private int boundaryNodeCount = 8;

        [ObservableProperty]
        private int electrodeCount = 8;

        [ObservableProperty]
        private int excitationElectrodeId = 1;

        [ObservableProperty]
        private int groundElectrodeId = 0;

        [ObservableProperty]
        private double excitationCurrentAmplitude = 10.0;

        [ObservableProperty]
        private double electrodeSurfaceLength = 0.1;

        [ObservableProperty]
        private double contactImpedance = 0.001;

        [ObservableProperty]
        private double inhomogenityValue = 2.0;

        [ObservableProperty]
        private int maxIterationCount = Workspace.MaxIterationCount;

        partial void OnMaxIterationCountChanged(int value) => Workspace.MaxIterationCount = value;

        [ObservableProperty]
        private double stepSize = Workspace.StepSize;

        partial void OnStepSizeChanged(double value) => Workspace.StepSize = value;

        [ObservableProperty]
        private double regularizationWeight = Workspace.RegularizationWeight;

        partial void OnRegularizationWeightChanged(double value) => Workspace.RegularizationWeight = value;
    }
}