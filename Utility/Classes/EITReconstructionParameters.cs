namespace Utility.Classes
{
    using Utility.Classes.ReconstructionParameters;
    
    public class EITReconstructionParameters
    {
        public DifferentialEquationSolver DifferentialEquationSolver { get; set; } = DifferentialEquationSolver.FiniteElementMethod;
        public RegularizationTechnique RegularizationTechnique { get; set; } = RegularizationTechnique.None;
        public ErrorMetric ErrorMetric { get; set; } = ErrorMetric.L2;
        public NumericSolver NumericSolver { get; set; } = NumericSolver.LUDecomposition;
        public NumericOptimizer NumericOptimizer { get; set; } = NumericOptimizer.GradientBased;

        public EITReconstructionParameters()
        {
            DifferentialEquationSolver = DifferentialEquationSolver.FiniteElementMethod;
            RegularizationTechnique = RegularizationTechnique.None;
            ErrorMetric = ErrorMetric.L2;
            NumericSolver = NumericSolver.LUDecomposition;
            NumericOptimizer = NumericOptimizer.GradientBased;
        }

        public EITReconstructionParameters(DifferentialEquationSolver differentialEquationSolver, RegularizationTechnique regularizationTechnique, ErrorMetric errorMetric, NumericSolver numericSolver, NumericOptimizer numericOptimizer)
        {
            DifferentialEquationSolver = differentialEquationSolver;
            RegularizationTechnique = regularizationTechnique;
            ErrorMetric = errorMetric;
            NumericSolver = numericSolver;
            NumericOptimizer = numericOptimizer;
        }
    }
}
