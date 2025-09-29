using Utility.Classes.Discretizer;
using CommunityToolkit.Mvvm.ComponentModel;
using Utility.Classes.Factories;
using Utility.Classes.Measurement;

namespace Utility.Classes.ReconstructionParameters
{
    /// <summary>
    /// Data transfer object that captures all user-configurable reconstruction options.
    /// </summary>
    public partial class EITReconstructionParameters : ObservableObject
    {
        [ObservableProperty]
        private DifferentialEquationSolver differentialEquationSolver = DifferentialEquationSolver.FEM;
        [ObservableProperty]
        private RegularizationTechnique regularizationTechnique = RegularizationTechnique.None;
        [ObservableProperty]
        private ErrorMetric errorMetric = ErrorMetric.Wasserstein2;
        [ObservableProperty]
        private NumericSolver numericSolver = NumericSolver.SVD;
        [ObservableProperty]
        private NumericOptimizer numericOptimizer = NumericOptimizer.ADAM;

        [ObservableProperty]
        private InitialDistributionTypes initialDistributionType = InitialDistributionTypes.Homogeneous;

        [ObservableProperty]
        private bool useOmpParallelization = false;

        [ObservableProperty]
        private MeasurementNoiseType measurementNoiseType = MeasurementNoiseType.None;

        [ObservableProperty]
        private double measurementNoiseAmplitude = 0.0;

        [ObservableProperty]
        private DrivePattern drivePattern = DrivePattern.Adjecent;

        public DiscretizationType Mesh = DiscretizationType.FEM;

        /// <summary>
        /// Creates a parameter set using the default reconstruction configuration.
        /// </summary>
        public EITReconstructionParameters()
        {
            DifferentialEquationSolver = DifferentialEquationSolver.FEM;
            RegularizationTechnique = RegularizationTechnique.None;
            ErrorMetric = ErrorMetric.Wasserstein2;
            NumericSolver = NumericSolver.SVD;
            NumericOptimizer = NumericOptimizer.ADAM;
            InitialDistributionType = InitialDistributionTypes.Homogeneous;
            MeasurementNoiseType = MeasurementNoiseType.None;
            MeasurementNoiseAmplitude = 0.0;
            DrivePattern = DrivePattern.Adjecent;
            Mesh = DiscretizationType.FEM;
            UseOmpParallelization = false;
        }

        /// <summary>
        /// Creates a parameter set with the provided solver, regularisation,
        /// and noise configuration.
        /// </summary>
        public EITReconstructionParameters(DifferentialEquationSolver differentialEquationSolver,
                                           RegularizationTechnique regularizationTechnique,
                                           ErrorMetric errorMetric,
                                           NumericSolver numericSolver,
                                           NumericOptimizer numericOptimizer,
                                           InitialDistributionTypes initialDistributionType = InitialDistributionTypes.SlightlyDiffering,
                                           MeasurementNoiseType measurementNoiseType = MeasurementNoiseType.None,
                                           double measurementNoiseAmplitude = 0.0,
                                           DrivePattern drivePattern = DrivePattern.Adjecent,
                                           bool useOmpParallelization = false)
        {
            DifferentialEquationSolver = differentialEquationSolver;
            RegularizationTechnique = regularizationTechnique;
            ErrorMetric = errorMetric;
            NumericSolver = numericSolver;
            NumericOptimizer = numericOptimizer;
            InitialDistributionType = initialDistributionType;
            MeasurementNoiseType = measurementNoiseType;
            MeasurementNoiseAmplitude = measurementNoiseAmplitude;
            DrivePattern = drivePattern;
            UseOmpParallelization = useOmpParallelization;

            Mesh = (differentialEquationSolver == DifferentialEquationSolver.FEM) ? DiscretizationType.FEM : DiscretizationType.LBM;
        }
    }
}
