using Utility.Classes.Meshing;

namespace Utility.Classes.Solvers
{
    /// <summary>
    /// Contains helper methods for applying differential operators to fields
    /// defined on a structured Lattice-Boltzmann grid using finite differences.
    /// </summary>
    public static class LatticeBoltzmannOperators
    {
        /// <summary>
        /// Calculates the gradient of a scalar field (potential/conductivity) on the LBM grid.
        /// Uses central differences for interior nodes and forward/backward differences at boundaries.
        /// </summary>
        /// <param name="mesh">The LBM mesh.</param>
        /// <param name="scalarField">The scalar field (e.g., a PotentialDistribution).</param>
        /// <returns>A VectorField representing the gradient.</returns>
        public static VectorField CalculateGradient(LBMMesh mesh, PotentialDistribution scalarField)
        {
            var gradientData = new Dictionary<int, (double Gx, double Gy)>();

            for (int y = 0; y < mesh.Ny; y++)
            {
                for (int x = 0; x < mesh.Nx; x++)
                {
                    double gx, gy;
                    int currentId = mesh.ToVertexId(x, y);

                    // Gradient in X
                    if (x == 0) // Forward difference at left edge
                        gx = scalarField.GetPotential(mesh.ToVertexId(x + 1, y)) - scalarField.GetPotential(currentId);
                    else if (x == mesh.Nx - 1) // Backward difference at right edge
                        gx = scalarField.GetPotential(currentId) - scalarField.GetPotential(mesh.ToVertexId(x - 1, y));
                    else // Central difference in interior
                        gx = (scalarField.GetPotential(mesh.ToVertexId(x + 1, y)) - scalarField.GetPotential(mesh.ToVertexId(x - 1, y))) / 2.0;

                    // Gradient in Y
                    if (y == 0) // Forward difference at bottom edge
                        gy = scalarField.GetPotential(mesh.ToVertexId(x, y + 1)) - scalarField.GetPotential(currentId);
                    else if (y == mesh.Ny - 1) // Backward difference at top edge
                        gy = scalarField.GetPotential(currentId) - scalarField.GetPotential(mesh.ToVertexId(x, y - 1));
                    else // Central difference in interior
                        gy = (scalarField.GetPotential(mesh.ToVertexId(x, y + 1)) - scalarField.GetPotential(mesh.ToVertexId(x, y - 1))) / 2.0;

                    gradientData[currentId] = (gx, gy);
                }
            }
            return new VectorField(gradientData);
        }

        /// <summary>
        /// Calculates the Laplacian of a scalar field on the LBM grid using the 5-point stencil.
        /// </summary>
        /// <param name="mesh">The LBM mesh.</param>
        /// <param name="scalarField">The scalar field (e.g., a PotentialDistribution).</param>
        /// <returns>A scalar field representing the Laplacian.</returns>
        public static PotentialDistribution CalculateLaplacian(LBMMesh mesh, PotentialDistribution scalarField)
        {
            var laplacianData = new Dictionary<int, double>();

            for (int y = 0; y < mesh.Ny; y++)
            {
                for (int x = 0; x < mesh.Nx; x++)
                {
                    int currentId = mesh.ToVertexId(x, y);

                    // Use zero for neighbors outside the boundary
                    double up = (y < mesh.Ny - 1) ? scalarField.GetPotential(mesh.ToVertexId(x, y + 1)) : 0;
                    double down = (y > 0) ? scalarField.GetPotential(mesh.ToVertexId(x, y - 1)) : 0;
                    double left = (x > 0) ? scalarField.GetPotential(mesh.ToVertexId(x - 1, y)) : 0;
                    double right = (x < mesh.Nx - 1) ? scalarField.GetPotential(mesh.ToVertexId(x + 1, y)) : 0;
                    double center = scalarField.GetPotential(currentId);

                    // 5-point stencil for discrete Laplacian (assuming h=1 grid spacing)
                    laplacianData[currentId] = right + left + up + down - 4 * center;
                }
            }
            return new PotentialDistribution(laplacianData);
        }

        /// <summary>
        /// Calculates the divergence of a vector field on the LBM grid.
        /// </summary>
        /// <param name="mesh">The LBM mesh.</param>
        /// <param name="vectorField">The vector field to compute the divergence of.</param>
        /// <returns>A scalar field representing the divergence.</returns>
        public static PotentialDistribution CalculateDivergence(LBMMesh mesh, VectorField vectorField)
        {
            var divergenceData = new Dictionary<int, double>();
            for (int y = 0; y < mesh.Ny; y++)
            {
                for (int x = 0; x < mesh.Nx; x++)
                {
                    // dFx/dx component
                    double dFx_dx;
                    if (x == 0)
                        dFx_dx = vectorField.GetVector(mesh.ToVertexId(x + 1, y)).X - vectorField.GetVector(mesh.ToVertexId(x, y)).X;
                    else if (x == mesh.Nx - 1)
                        dFx_dx = vectorField.GetVector(mesh.ToVertexId(x, y)).X - vectorField.GetVector(mesh.ToVertexId(x - 1, y)).X;
                    else
                        dFx_dx = (vectorField.GetVector(mesh.ToVertexId(x + 1, y)).X - vectorField.GetVector(mesh.ToVertexId(x - 1, y)).X) / 2.0;

                    // dFy/dy component
                    double dFy_dy;
                    if (y == 0)
                        dFy_dy = vectorField.GetVector(mesh.ToVertexId(x, y + 1)).Y - vectorField.GetVector(mesh.ToVertexId(x, y)).Y;
                    else if (y == mesh.Ny - 1)
                        dFy_dy = vectorField.GetVector(mesh.ToVertexId(x, y)).Y - vectorField.GetVector(mesh.ToVertexId(x, y - 1)).Y;
                    else
                        dFy_dy = (vectorField.GetVector(mesh.ToVertexId(x, y + 1)).Y - vectorField.GetVector(mesh.ToVertexId(x, y - 1)).Y) / 2.0;

                    divergenceData[mesh.ToVertexId(x, y)] = dFx_dx + dFy_dy;
                }
            }
            return new PotentialDistribution(divergenceData);
        }
    }
}
