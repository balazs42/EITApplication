using Utility.Classes.Discretizer;
using CommunityToolkit.Mvvm.ComponentModel;
using Utility.Classes.Factories;

namespace Utility.Classes.ReconstructionParameters
{
    public partial class EITReconstructionParameters : ObservableObject
    {
        [ObservableProperty]
        private DifferentialEquationSolver differentialEquationSolver = DifferentialEquationSolver.FiniteElementMethod;
        [ObservableProperty]
        private RegularizationTechnique regularizationTechnique = RegularizationTechnique.None;
        [ObservableProperty]
        private ErrorMetric errorMetric = ErrorMetric.L2;
        [ObservableProperty]
        private NumericSolver numericSolver = NumericSolver.LUDecomposition;
        [ObservableProperty]
        private NumericOptimizer numericOptimizer = NumericOptimizer.GradientBased;

        [ObservableProperty]
        private InitialDistributionTypes initialDistributionType = InitialDistributionTypes.SlightlyDiffering;

        public DiscretizationType Mesh = DiscretizationType.FEM;

        public EITReconstructionParameters()
        {
            DifferentialEquationSolver = DifferentialEquationSolver.FiniteElementMethod;
            RegularizationTechnique = RegularizationTechnique.None;
            ErrorMetric = ErrorMetric.L2;
            NumericSolver = NumericSolver.LUDecomposition;
            NumericOptimizer = NumericOptimizer.GradientBased;
            InitialDistributionType = InitialDistributionTypes.SlightlyDiffering;
            Mesh = DiscretizationType.FEM;
        }

        public EITReconstructionParameters(DifferentialEquationSolver differentialEquationSolver, 
                                           RegularizationTechnique regularizationTechnique,
                                           ErrorMetric errorMetric,
                                           NumericSolver numericSolver,
                                           NumericOptimizer numericOptimizer,
                                           InitialDistributionTypes initialDistributionType = InitialDistributionTypes.SlightlyDiffering)
        {
            DifferentialEquationSolver = differentialEquationSolver;
            RegularizationTechnique = regularizationTechnique;
            ErrorMetric = errorMetric;
            NumericSolver = numericSolver;
            NumericOptimizer = numericOptimizer;
            InitialDistributionType = initialDistributionType;

            Mesh = (differentialEquationSolver == DifferentialEquationSolver.FiniteElementMethod) ? DiscretizationType.FEM : DiscretizationType.LBM;
        }
    }
}
