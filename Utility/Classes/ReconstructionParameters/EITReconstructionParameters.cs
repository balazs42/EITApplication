using CommunityToolkit.Mvvm.ComponentModel;

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

        public EITReconstructionParameters()
        {
            DifferentialEquationSolver = DifferentialEquationSolver.FiniteElementMethod;
            RegularizationTechnique = RegularizationTechnique.None;
            ErrorMetric = ErrorMetric.L2;
            NumericSolver = NumericSolver.LUDecomposition;
            NumericOptimizer = NumericOptimizer.GradientBased;
        }

        public EITReconstructionParameters(DifferentialEquationSolver differentialEquationSolver, 
                                           RegularizationTechnique regularizationTechnique, 
                                           ErrorMetric errorMetric, 
                                           NumericSolver numericSolver, 
                                           NumericOptimizer numericOptimizer)
        {
            DifferentialEquationSolver = differentialEquationSolver;
            RegularizationTechnique = regularizationTechnique;
            ErrorMetric = errorMetric;
            NumericSolver = numericSolver;
            NumericOptimizer = numericOptimizer;
        }
    }
}
