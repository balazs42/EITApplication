using Utility.Classes.ReconstructionParameters;

using Workspace = Utility.Classes.Application.Workspace;

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
            NumericSolver.LU => CreateLUDecompositionSolver(),
            NumericSolver.SVD => CreateSVDSolver(),
            NumericSolver.tSVD => CreatetSVDSolver(),
            NumericSolver.GMRES => CreateGMRESSolver(),
            _ => throw new NotSupportedException()
        };

        private static LuDecompositionSolver CreateLUDecompositionSolver() 
        {
            var solver = new LuDecompositionSolver();

            Workspace.AddLogMessage("NumericSolverFactory","Created LU Decomposition Numeric Solver object.");

            return solver;
        }
        private static SVDSolver CreateSVDSolver() 
        {
            var solver = new SVDSolver();

            Workspace.AddLogMessage("NumericSolverFactory","Created SVD Numeric Solver object.");

            return solver;
        }
        private static tSVDSolver CreatetSVDSolver() 
        {
            var solver = new tSVDSolver();

            Workspace.AddLogMessage("NumericSolverFactory","Created tSVD Numeric Solver object.");

            return solver;
        }
        private static GmresSolver CreateGMRESSolver()
        {
            var solver = new GmresSolver();

            Workspace.AddLogMessage("NumericSolverFactory","Created GRMES Numeric Solver object.");

            return solver;
        }        
    }
}
