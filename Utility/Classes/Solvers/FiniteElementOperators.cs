using Utility.Classes.Meshing;

namespace Utility.Classes.Solvers
{
    public static class FiniteElementOperators
    {
        /// <summary>
        /// Calculates the gradient of a scalar field, assuming linear basis functions.
        /// The gradient is constant within each element.
        /// This matches the derivation in your thesis.
        /// </summary>
        /// <param name="femMesh">The FEM mesh.</param>
        /// <param name="scalarField">A scalar field defined per-vertex (e.g., PotentialDistribution).</param>
        /// <returns>A VectorField where the key is the ElementId.</returns>
        public static VectorField CalculateElementWiseGradient(FEMMesh femMesh, PotentialDistribution scalarField)
        {
            var gradientData = new Dictionary<int, (double Gx, double Gy)>();

            foreach (var element in femMesh.Elements)
            {
                var v1 = element.V1;
                var v2 = element.V2;
                var v3 = element.V3;

                // Potentials at the vertices of the element
                double s1 = scalarField.GetPotential(v1.GlobalId);
                double s2 = scalarField.GetPotential(v2.GlobalId);
                double s3 = scalarField.GetPotential(v3.GlobalId);

                // Denominator for shape function derivatives: 2 * Area
                double twoA = 2 * element.Area;
                if (Math.Abs(twoA) < 1e-12) continue; // Avoid division by zero for degenerate triangles

                // Gradients of the shape functions (N1, N2, N3)
                double dN1dx = (v2.Y - v3.Y) / twoA;
                double dN1dy = (v3.X - v2.X) / twoA;
                double dN2dx = (v3.Y - v1.Y) / twoA;
                double dN2dy = (v1.X - v3.X) / twoA;
                double dN3dx = (v1.Y - v2.Y) / twoA;
                double dN3dy = (v2.X - v1.X) / twoA;

                // Gradient of the field σ = s1*N1 + s2*N2 + s3*N3
                // ∇σ = s1*∇N1 + s2*∇N2 + s3*∇N3
                double gx = s1 * dN1dx + s2 * dN2dx + s3 * dN3dx;
                double gy = s1 * dN1dy + s2 * dN2dy + s3 * dN3dy;

                gradientData[element.Id] = (gx, gy);
            }

            return new VectorField(gradientData);
        }

        /// <summary>
        /// Computes the discrete Laplacian of a scalar field using the cotangent formula.
        /// This is a common and robust method for unstructured triangular meshes.
        /// The scalar field must be defined per-vertex.
        /// </summary>
        /// <param name="femMesh">The FEM mesh.</param>
        /// <param name="scalarField">A scalar field defined per-vertex.</param>
        /// <returns>A scalar field representing the Laplacian at each vertex.</returns>
        public static PotentialDistribution CalculateLaplacian(FEMMesh femMesh, PotentialDistribution scalarField)
        {
            var laplacianData = new Dictionary<int, double>();
            var adjacency = BuildAdjacencyMap(femMesh);

            foreach (var vertex in femMesh.Vertices)
            {
                int i = vertex.GlobalId;
                double laplacianValue = 0.0;

                if (!adjacency.ContainsKey(i)) continue;

                foreach (int j in adjacency[i]) // For each neighbor j of vertex i
                {
                    double s_i = scalarField.GetPotential(i);
                    double s_j = scalarField.GetPotential(j);

                    // Find the two triangles sharing the edge (i, j)
                    var sharedTriangles = femMesh.Elements.Where(e =>
                        (e.V1.GlobalId == i && (e.V2.GlobalId == j || e.V3.GlobalId == j)) ||
                        (e.V2.GlobalId == i && (e.V1.GlobalId == j || e.V3.GlobalId == j)) ||
                        (e.V3.GlobalId == i && (e.V1.GlobalId == j || e.V2.GlobalId == j))
                    ).ToList();

                    double cotAlpha = 0;
                    double cotBeta = 0;

                    if (sharedTriangles.Count > 0)
                    {
                        var p_k = sharedTriangles[0].V1.GlobalId != i && sharedTriangles[0].V1.GlobalId != j ? sharedTriangles[0].V1 :
                                  sharedTriangles[0].V2.GlobalId != i && sharedTriangles[0].V2.GlobalId != j ? sharedTriangles[0].V2 :
                                  sharedTriangles[0].V3;
                        cotAlpha = Cotangent(femMesh.Vertices[i], femMesh.Vertices[j], p_k);
                    }
                    if (sharedTriangles.Count > 1)
                    {
                        var p_l = sharedTriangles[1].V1.GlobalId != i && sharedTriangles[1].V1.GlobalId != j ? sharedTriangles[1].V1 :
                                  sharedTriangles[1].V2.GlobalId != i && sharedTriangles[1].V2.GlobalId != j ? sharedTriangles[1].V2 :
                                  sharedTriangles[1].V3;
                        cotBeta = Cotangent(femMesh.Vertices[i], femMesh.Vertices[j], p_l);
                    }

                    laplacianValue += (cotAlpha + cotBeta) * (s_j - s_i);
                }
                laplacianData[i] = 0.5 * laplacianValue;
            }

            return new PotentialDistribution(laplacianData);
        }

        #region Private Helpers

        private static double Cotangent(Vertex p1, Vertex p2, Vertex p3)
        {
            // Calculate cotangent of the angle at p3 for the triangle p1-p2-p3
            double v1x = p1.X - p3.X;
            double v1y = p1.Y - p3.Y;
            double v2x = p2.X - p3.X;
            double v2y = p2.Y - p3.Y;

            double dotProduct = v1x * v2x + v1y * v2y;
            // Using Cross Product for sin: |v1 x v2| = |v1| |v2| sin(theta) -> in 2D, |v1x*v2y - v1y*v2x|
            double crossProductMagnitude = v1x * v2y - v1y * v2x;

            return Math.Abs(crossProductMagnitude) < 1e-12 ? 0 : dotProduct / crossProductMagnitude;
        }

        private static Dictionary<int, List<int>> BuildAdjacencyMap(FEMMesh mesh)
        {
            var adjacency = new Dictionary<int, List<int>>();
            foreach (var element in mesh.Elements)
            {
                AddEdge(adjacency, element.V1.GlobalId, element.V2.GlobalId);
                AddEdge(adjacency, element.V2.GlobalId, element.V3.GlobalId);
                AddEdge(adjacency, element.V3.GlobalId, element.V1.GlobalId);
            }
            return adjacency;
        }

        private static void AddEdge(Dictionary<int, List<int>> adjacency, int u, int v)
        {
            if (!adjacency.ContainsKey(u)) adjacency[u] = [];
            if (!adjacency.ContainsKey(v)) adjacency[v] = [];
            if (!adjacency[u].Contains(v)) adjacency[u].Add(v);
            if (!adjacency[v].Contains(u)) adjacency[v].Add(u);
        }

        #endregion
    }
}