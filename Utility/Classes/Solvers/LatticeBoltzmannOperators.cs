using Utility.Classes.Meshing;

namespace Utility.Classes.Solvers
{
    /// <summary>
    /// Contains helper methods for applying discrete differential operators to fields
    /// defined on a structured Lattice-Boltzmann grid.
    /// This implementation is "element-centric", meaning it uses the neighbor-linking
    /// of the LBMElement class rather than coordinate-based lookups.
    /// </summary>
    public static class LatticeBoltzmannOperators
    {
        /// <summary>
        /// Calculates the gradient of a scalar field (∇φ) on the LBM grid.
        /// The gradient is a vector field, where each element has a (Gx, Gy) vector.
        /// It uses central differences for interior nodes and less accurate
        /// forward/backward differences at boundaries where a neighbor is missing.
        /// </summary>
        /// <param name="mesh">The LBM mesh, with linked neighbors.</param>
        /// <param name="scalarField">The scalar field (e.g., a PotentialDistribution) to differentiate.</param>
        /// <returns>A VectorField representing the gradient.</returns>
        public static VectorField CalculateGradient(LBMMesh mesh, PotentialDistribution scalarField)
        {
            var gradientData = new Dictionary<int, (double Gx, double Gy)>();

            // Iterate over every element in the mesh directly.
            foreach (var element in mesh.Elements.Cast<LBMElement>())
            {
                // Get the neighbors in the cardinal directions from the pre-linked array.
                var right = element.Neighbors[1];
                var top = element.Neighbors[2];
                var left = element.Neighbors[3];
                var bottom = element.Neighbors[4];

                // Get the potential (phi) value for the center and neighboring cells.
                double phi_i = scalarField.GetPotential(element.Id);

                // If a neighbor is null (i.e., we are at a boundary), use the center
                // point's value. This results in a zero gradient contribution from that side.
                double phi_r = right != null ? scalarField.GetPotential(right.Id) : phi_i;
                double phi_l = left != null ? scalarField.GetPotential(left.Id) : phi_i;
                double phi_t = top != null ? scalarField.GetPotential(top.Id) : phi_i;
                double phi_b = bottom != null ? scalarField.GetPotential(bottom.Id) : phi_i;

                // Calculate the partial derivative ∂φ/∂x using finite differences.
                // If both neighbors exist, use central difference: (f(x+h) - f(x-h)) / 2h.
                // If one is missing, use forward/backward difference: (f(x+h) - f(x)) / h.
                // We assume grid spacing h=1.
                double gx = (phi_r - phi_l) / (right != null && left != null ? 2.0 : 1.0);
                double gy = (phi_t - phi_b) / (top != null && bottom != null ? 2.0 : 1.0);

                gradientData[element.Id] = (gx, gy);
            }
            return new VectorField(gradientData);
        }

        /// <summary>
        /// Calculates the Laplacian of a scalar field (Δφ) on the LBM grid.
        /// The Laplacian is a scalar field, representing the divergence of the gradient.
        /// It uses the standard 5-point finite difference stencil.
        /// </summary>
        /// <param name="mesh">The LBM mesh, with linked neighbors.</param>
        /// <param name="scalarField">The scalar field to apply the operator to.</param>
        /// <returns>A scalar field (PotentialDistribution) representing the Laplacian.</returns>
        public static PotentialDistribution CalculateLaplacian(LBMMesh mesh, PotentialDistribution scalarField)
        {
            var laplacianData = new Dictionary<int, double>();

            // Iterate over every element in the mesh.
            foreach (var element in mesh.Elements.Cast<LBMElement>())
            {
                var right = element.Neighbors[1];
                var top = element.Neighbors[2];
                var left = element.Neighbors[3];
                var bottom = element.Neighbors[4];

                // Get potential values, using the center value for any missing boundary neighbors.
                double phi_i = scalarField.GetPotential(element.Id);
                double phi_r = right != null ? scalarField.GetPotential(right.Id) : phi_i;
                double phi_l = left != null ? scalarField.GetPotential(left.Id) : phi_i;
                double phi_t = top != null ? scalarField.GetPotential(top.Id) : phi_i;
                double phi_b = bottom != null ? scalarField.GetPotential(bottom.Id) : phi_i;

                // Apply the 5-point stencil for the discrete Laplacian: Δφ ≈ φ_r + φ_l + φ_t + φ_b - 4φ_i
                // (Assuming grid spacing h=1).
                laplacianData[element.Id] = phi_r + phi_l + phi_t + phi_b - 4 * phi_i;
            }
            return new PotentialDistribution(laplacianData);
        }

        /// <summary>
        /// Calculates the divergence of a vector field (∇·F) on the LBM grid.
        /// The divergence of a vector field is a scalar field: ∇·F = ∂Fx/∂x + ∂Fy/∂y.
        /// </summary>
        /// <param name="mesh">The LBM mesh, with linked neighbors.</param>
        /// <param name="vectorField">The vector field to compute the divergence of.</param>
        /// <returns>A scalar field (PotentialDistribution) representing the divergence.</returns>
        public static PotentialDistribution CalculateDivergence(LBMMesh mesh, VectorField vectorField)
        {
            var divergenceData = new Dictionary<int, double>();

            // Iterate over every element in the mesh.
            foreach (var element in mesh.Elements.Cast<LBMElement>())
            {
                // Get neighbors in the cardinal directions.
                var right = element.Neighbors[1];
                var top = element.Neighbors[2];
                var left = element.Neighbors[3];
                var bottom = element.Neighbors[4];

                // Get the vector F = (Fx, Fy) for the center and neighboring cells.
                var F_i = vectorField.GetVector(element.Id);
                var F_r = right != null ? vectorField.GetVector(right.Id) : F_i;
                var F_l = left != null ? vectorField.GetVector(left.Id) : F_i;
                var F_t = top != null ? vectorField.GetVector(top.Id) : F_i;
                var F_b = bottom != null ? vectorField.GetVector(bottom.Id) : F_i;

                // Calculate the partial derivative ∂Fx/∂x using the X-components of the vectors.
                double dFx_dx = (F_r.X - F_l.X) / (right != null && left != null ? 2.0 : 1.0);

                // Calculate the partial derivative ∂Fy/∂y using the Y-components of the vectors.
                double dFy_dy = (F_t.Y - F_b.Y) / (top != null && bottom != null ? 2.0 : 1.0);

                // The divergence is the sum of the partial derivatives.
                divergenceData[element.Id] = dFx_dx + dFy_dy;
            }
            return new PotentialDistribution(divergenceData);
        }
    }
}