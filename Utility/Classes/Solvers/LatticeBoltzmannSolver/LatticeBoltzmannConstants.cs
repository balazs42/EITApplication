namespace Utility.Classes.Solvers.LatticeBoltzmannSolver
{
    internal static class LatticeBoltzmannConstants
    {
        public static readonly (int cx, int cy)[] Directions =
        {
            (0, 0), (1, 0), (0, 1), (-1, 0), (0, -1), (1, 1), (-1, 1), (-1, -1), (1, -1)
        };

        public static readonly int[] Opposite = { 0, 3, 4, 1, 2, 7, 8, 5, 6 };

        public static readonly double[] Weights =
        {
            4.0 / 9.0,
            1.0 / 9.0, 1.0 / 9.0, 1.0 / 9.0, 1.0 / 9.0,
            1.0 / 36.0, 1.0 / 36.0, 1.0 / 36.0, 1.0 / 36.0
        };

        public const double CsSquared = 1.0 / 3.0;
    }
}
