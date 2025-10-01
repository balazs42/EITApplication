namespace Utility.Classes.Solvers.LatticeBoltzmannSolver
{
    /// <summary>
    /// Contains fundamental constants for the D2Q9 Lattice Boltzmann Method implementation.
    /// D2Q9 means 2-dimensional with 9 discrete velocity directions.
    /// </summary>
    internal static class LatticeBoltzmannConstants
    {
        /// <summary>
        /// The 9 discrete velocity directions in D2Q9 lattice structure.
        /// Index 0: Rest particle (0,0) - center cell
        /// Index 1-4: Cardinal directions - right, up, left, down
        /// Index 5-8: Diagonal directions - upper-right, upper-left, lower-left, lower-right
        /// These directions define how particles propagate between neighboring cells.
        /// </summary>
        public static readonly (int cx, int cy)[] Directions =
        {
            (0, 0),   // 0: Rest particle (stays in place)
            (1, 0),   // 1: Move right (positive X)
            (0, 1),   // 2: Move up (positive Y)
            (-1, 0),  // 3: Move left (negative X)
            (0, -1),  // 4: Move down (negative Y)
            (1, 1),   // 5: Move diagonally up-right
            (-1, 1),  // 6: Move diagonally up-left
            (-1, -1), // 7: Move diagonally down-left
            (1, -1)   // 8: Move diagonally down-right
        };

        /// <summary>
        /// Maps each direction to its opposite direction for bounce-back boundary conditions.
        /// When a particle hits a wall, it bounces back in the opposite direction.
        /// For example: right (index 1) bounces to left (index 3).
        /// This is crucial for implementing no-slip boundary conditions at walls.
        /// </summary>
        public static readonly int[] Opposite = { 0, 3, 4, 1, 2, 7, 8, 5, 6 };

        /// <summary>
        /// Equilibrium distribution weights for each velocity direction in D2Q9.
        /// These weights ensure isotropy and Galilean invariance of the method.
        /// Weight[0] = 4/9 for rest particle (highest probability)
        /// Weight[1-4] = 1/9 for cardinal directions
        /// Weight[5-8] = 1/36 for diagonal directions (lowest probability)
        /// Sum of all weights equals 1, ensuring conservation of mass.
        /// </summary>
        public static readonly double[] Weights =
        {
            4.0 / 9.0,    // Rest particle - highest weight
            1.0 / 9.0, 1.0 / 9.0, 1.0 / 9.0, 1.0 / 9.0,        // Cardinal directions
            1.0 / 36.0, 1.0 / 36.0, 1.0 / 36.0, 1.0 / 36.0     // Diagonal directions
        };

        /// <summary>
        /// Square of the lattice speed of sound (cs?) for D2Q9 lattice.
        /// This fundamental constant relates the discrete lattice to physical units.
        /// It's used in the BGK collision operator and relaxation time calculation.
        /// Value of 1/3 ensures correct recovery of the diffusion equation in the continuum limit.
        /// </summary>
        public const double CsSquared = 1.0 / 3.0;
    }
}
