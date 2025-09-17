using Utility.Classes.Discretizer;
using CommunityToolkit.Mvvm.ComponentModel;
using Utility.Classes.Factories;

namespace Utility.Classes.ReconstructionParameters
{
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

        public DiscretizationType Mesh = DiscretizationType.FEM;

        public EITReconstructionParameters()
        {
            DifferentialEquationSolver = DifferentialEquationSolver.FEM;
            RegularizationTechnique = RegularizationTechnique.None;
            ErrorMetric = ErrorMetric.Wasserstein2;
            NumericSolver = NumericSolver.SVD;
            NumericOptimizer = NumericOptimizer.ADAM;
            InitialDistributionType = InitialDistributionTypes.Homogeneous;
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

            Mesh = (differentialEquationSolver == DifferentialEquationSolver.FEM) ? DiscretizationType.FEM : DiscretizationType.LBM;
        }
    }
}
