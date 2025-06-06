using Utility.Classes.ReconstructionParameters;

namespace Utility.Classes.Factories
{
    public static class NumericSolverFactory
    {
        public static INumericSolver Create(NumericSolver ns) => ns switch
        {
            NumericSolver.LUDecomposition => new LuDecompositionSolver(),
            NumericSolver.SVD => new SVDSolver(),
            NumericSolver.tSVD => new tSVDSolver(),
            NumericSolver.GMRES => new GmresSolver(),
            _ => throw new NotSupportedException()
        };
    }
}
