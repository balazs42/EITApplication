using Utility.Classes.Discretizer;
using Utility.Classes.Discretizer.FiniteElementMesh;
using Utility.Classes.Factories;
using Utility.Classes.Measurement;
using Utility.Classes.Reconstruction;
using Utility.Classes.Reconstruction.Convexification;
using Utility.Classes.Reconstruction.VirtualElectrodes;
using Utility.Classes.Solvers;
using Utility.Classes.Solvers.FiniteElementSolver;
using CommunityToolkit.Mvvm.ComponentModel;
using System.Collections.Generic;
using Utility.Classes.Configurations.ReconstructionConfiguration;

namespace Utility.Classes.ReconstructionParameters
{
    /// <summary>
    /// Data transfer object that captures all user-configurable reconstruction options and runtime
    /// components materialized from either the classic parameters or the block configuration pipeline.
    /// </summary>
    public partial class ReconstructionRuntimeContext : ObservableObject
    {
        [ObservableProperty]
        private DifferentialEquationSolver differentialEquationSolver = DifferentialEquationSolver.FEM;
        [ObservableProperty]
        private RegularizationTechnique regularizationTechnique = RegularizationTechnique.None;
        [ObservableProperty]
        private ErrorMetric errorMetric = ErrorMetric.Wasserstein2;
        [ObservableProperty]
        private NumericSolver numericSolver = NumericSolver.GMRES;
        [ObservableProperty]
        private NumericOptimizer numericOptimizer = NumericOptimizer.ADAM;

        [ObservableProperty]
        private InitialDistributionTypes initialDistributionType = InitialDistributionTypes.Homogeneous;

        [ObservableProperty]
        private MeasurementNoiseType measurementNoiseType = MeasurementNoiseType.None;

        [ObservableProperty]
        private double measurementNoiseAmplitude = 0.0;

        [ObservableProperty]
        private double contactImpedanceOhms = 0.1;

        [ObservableProperty]
        private double contactImpedanceVariation = 0.0;

        [ObservableProperty]
        private DrivePattern drivePattern = DrivePattern.Adjecent;

        [ObservableProperty]
        private int drivePatternSkip = 0;

        [ObservableProperty]
        private bool usePotentialDifferences = false;

        [ObservableProperty]
        private bool useOmpParallelization = false;

        [ObservableProperty]
        private bool useCudaAcceleration = false;

        [ObservableProperty]
        private bool useLbmGaussianFilter = false;

        [ObservableProperty]
        private bool useLbmConductivityFilter = false;

        [ObservableProperty]
        private int lbmGaussianFilterSize = 3;

        [ObservableProperty]
        private int lbmConductivityFilterInterval = 5;

        [ObservableProperty]
        private double conductivityMinimumBound = 0.1;

        [ObservableProperty]
        private double conductivityMaximumBound = 10.0;

        [ObservableProperty]
        private VirtualElectrodeSettings virtualElectrodeSettings = new();

        public DiscretizationType Mesh = DiscretizationType.FEM;

        [ObservableProperty]
        private bool useCurtisImigranMorrowPresolve;

        [ObservableProperty]
        private double initializationCurrentAmplitude = 1.0;

        [ObservableProperty]
        private bool solveInitializationInComplexDomain;

        [ObservableProperty]
        private double lbmPhysicalDomainSize = 1.0;

        [ObservableProperty]
        private LatticeBoltzmannRelaxationModel lbmRelaxationModel = LatticeBoltzmannRelaxationModel.BGK;

        /// <summary>
        /// Reconstruction-specific tuning parameters for the convexification path.
        /// Stored alongside the legacy runtime context so services can reuse the
        /// existing workspace, measurement and FEM initialization pipeline.
        /// These options are authoritative for the convexification solver and
        /// must not be overwritten by legacy page-level step-size settings.
        /// </summary>
        public ConvexificationOptions ConvexificationOptions { get; set; } = new();

        /// <summary>
        /// Runtime references constructed from the block-based pipeline when available.
        /// </summary>
        public FEMMesh? RuntimeMesh { get; set; }
        public IDifferentialEquationSolver? RuntimeDifferentialEquationSolver { get; set; }
        public INumericSolver? RuntimeNumericSolver { get; set; }
        public List<(string id, double connectionWeight, IRegularizer regulizer)> Regularizers { get; set; } = new();
        public List<(string id, double connectionWeight, IErrorMetric errorMetric)> ErrorMetrics { get; set; } = new();
        public List<(string id, double connectionWeight, INumericOptimizer numericOptimizer)> NumericOptimizers { get; set; } = new();
        public ConductivityDistribution? OriginalDistribution { get; set; }
        public ConductivityDistribution? InitialDistribution { get; set; }
        public ElectrodeMeasurementSetup MeasurementSetup { get; set; } = ElectrodeMeasurementSetup.Active;
        public IReadOnlyList<WeightedConnectionSnapshot>? AllConnections { get; set; }

        /// <summary>
        /// Creates a parameter set using the default reconstruction configuration.
        /// </summary>
        public ReconstructionRuntimeContext()
        {
            DifferentialEquationSolver = DifferentialEquationSolver.FEM;
            RegularizationTechnique = RegularizationTechnique.None;
            ErrorMetric = ErrorMetric.Wasserstein2;
            NumericSolver = NumericSolver.GMRES;
            NumericOptimizer = NumericOptimizer.ADAM;
            InitialDistributionType = InitialDistributionTypes.Homogeneous;
            MeasurementNoiseType = MeasurementNoiseType.None;
            MeasurementNoiseAmplitude = 0.0;
            DrivePattern = DrivePattern.Adjecent;
            DrivePatternSkip = 0;
            UsePotentialDifferences = false;
            UseOmpParallelization = false;
            UseCudaAcceleration = false;
            Mesh = DiscretizationType.FEM;
        }

        /// <summary>
        /// Creates a parameter set with the provided solver, regularisation,
        /// and noise configuration.
        /// </summary>
        public ReconstructionRuntimeContext(DifferentialEquationSolver differentialEquationSolver,
                                           RegularizationTechnique regularizationTechnique,
                                           ErrorMetric errorMetric,
                                           NumericSolver numericSolver,
                                           NumericOptimizer numericOptimizer,
                                            InitialDistributionTypes initialDistributionType = InitialDistributionTypes.SlightlyDiffering,
                                            MeasurementNoiseType measurementNoiseType = MeasurementNoiseType.None,
                                            double measurementNoiseAmplitude = 0.0,
                                            DrivePattern drivePattern = DrivePattern.Adjecent,
                                            int drivePatternSkip = 0,
                                            bool usePotentialDifferences = false)
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
            DrivePatternSkip = drivePatternSkip;
            UsePotentialDifferences = usePotentialDifferences;
            UseOmpParallelization = false;
            UseCudaAcceleration = false;

            Mesh = (differentialEquationSolver == DifferentialEquationSolver.FEM) ? DiscretizationType.FEM : DiscretizationType.LBM;
        }

        partial void OnDrivePatternSkipChanged(int value)
        {
            if (value < 0)
                DrivePatternSkip = 0;
        }
    }
}
