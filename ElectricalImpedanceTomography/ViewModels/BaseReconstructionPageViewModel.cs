using CommunityToolkit.Mvvm.ComponentModel;
using Utility.Classes.ReconstructionParameters;
using Workspace = Utility.Classes.Application.Workspace;

namespace ElectricalImpedanceTomography.ViewModels
{
    public partial class BaseReconstructionPageViewModel : BaseViewModel
    {
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
        private EITReconstructionParameters reconstructionParameters = Workspace.GetReconstructionParameters();

        partial void OnReconstructionParametersChanged(EITReconstructionParameters value)
        {
            Workspace.SetReconstructionParameters(value);
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
        private double excitationCurrentAmplitude = 1.0;

        [ObservableProperty]
        private double electrodeSurfaceLength = 1.0;

        [ObservableProperty]
        private double contactImpedance = 1.0;

        [ObservableProperty]
        private double inhomogenityValue = 2.0;

        [ObservableProperty]
        private int maxIterationCount = 50;

        [ObservableProperty]
        private double stepSize = 0.001;

        [ObservableProperty]
        private double regularizationWeight = 1e-3;
    }
}
