using System.Linq;

namespace Utility.Classes.Solvers.GraphBasedSolver
{
    /// <summary>
    /// Simple smoothing helpers for graph conductances used in the
    /// graph-based EIT solver.
    /// </summary>
    public static class GraphRegularization
    {
        /// <summary>
        ///     Projects a vector of conductances onto the convex cone
        ///     <c>g_e ≥ 0</c> by clamping negative entries to zero.  This
        ///     enforces the physical positivity constraint on edge weights in
        ///     resistor networks.
        /// </summary>
        /// <param name="g">Input conductance array.</param>
        /// <returns>Array with all negative values replaced by zero.</returns>
        public static double[] ProjectPositive(double[] g) => g.Select(x => x < 0 ? 0 : x).ToArray();
    }
}
