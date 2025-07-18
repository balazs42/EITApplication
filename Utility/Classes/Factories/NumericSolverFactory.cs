using Utility.Classes.ReconstructionParameters;

namespace Utility.Classes.Factories
{
    /// <summary>
    /// The numerical solver factory should be used to create the appropriate solver for the 
    /// set of linear equations arising in any step of the solution.
    /// </summary>
    public static class NumericSolverFactory
    {
        public static INumericSolver Create(NumericSolver ns) => ns switch
        {
            NumericSolver.LUDecomposition => CreateLUDecompositionSolver(),
            NumericSolver.SVD => CreateSVDSolver(),
            NumericSolver.tSVD => CreatetSVDSolver(),
            NumericSolver.GMRES => CreateGMRESSolver(),
            _ => throw new NotSupportedException()
        };

        private static LuDecompositionSolver CreateLUDecompositionSolver() => new LuDecompositionSolver();
        private static SVDSolver CreateSVDSolver() => new SVDSolver();
        private static tSVDSolver CreatetSVDSolver() => new tSVDSolver();
        private static GmresSolver CreateGMRESSolver() => new GmresSolver();
        
    }
}
