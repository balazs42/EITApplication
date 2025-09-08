using CommunityToolkit.Mvvm.ComponentModel;
using Utility.Classes.ReconstructionParameters;
using Utility.Classes.Factories;

using Workspace = Utility.Classes.Application.Workspace;

namespace ElectricalImpedanceTomography.ViewModels
{
    public partial class BaseReconstructionPageViewModel : BaseViewModel
    {
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

        [ObservableProperty]
        private EITReconstructionParameters reconstructionParameters = Workspace.GetReconstructionParameters();

        partial void OnReconstructionParametersChanged(EITReconstructionParameters value) => Workspace.SetReconstructionParameters(value);

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
        private double excitationCurrentAmplitude = 1.0;

        [ObservableProperty]
        private double electrodeSurfaceLength = 1.0;

        [ObservableProperty]
        private double contactImpedance = 1.0;

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