using CommunityToolkit.Mvvm.ComponentModel;
using Utility.Classes.Meshing;

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

        public MeshType Mesh = MeshType.FEM;

        public EITReconstructionParameters()
        {
            DifferentialEquationSolver = DifferentialEquationSolver.FiniteElementMethod;
            RegularizationTechnique = RegularizationTechnique.None;
            ErrorMetric = ErrorMetric.L2;
            NumericSolver = NumericSolver.LUDecomposition;
            NumericOptimizer = NumericOptimizer.GradientBased;
            Mesh = MeshType.FEM;
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

            Mesh = (differentialEquationSolver == DifferentialEquationSolver.FiniteElementMethod) ? MeshType.FEM : MeshType.LBM;
        }
    }
}
