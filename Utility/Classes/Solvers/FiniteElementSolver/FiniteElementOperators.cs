using Utility.Classes.Discretizer.FiniteElementMesh;
using Utility.Classes.Solvers;

namespace Utility.Classes.Solvers.FiniteElementSolver
{
    public static class FiniteElementOperators
    {
        /// <summary>
        /// Calculates the gradient of a scalar field, assuming linear basis functions.
        /// The gradient is constant within each element.
        /// </summary>
        /// <param name="femMesh">The FEM mesh.</param>
        /// <param name="scalarField">A scalar field defined per-FEMVertex (e.g., PotentialDistribution).</param>
        /// <returns>A VectorField where the key is the ElementId.</returns>
        public static VectorField CalculateElementWiseGradient(FEMMesh femMesh, ScalarField scalarField)
        {
            var gradientData = new Dictionary<int, (double Gx, double Gy)>();
            var elements = femMesh.GetElements().Cast<FEMElement>();

            foreach (var element in elements)
            {
                var v1 = element.Vertices[0];
                var v2 = element.Vertices[1];
                var v3 = element.Vertices[2];

                // Potentials at the vertices of the element
                double s1 = scalarField.GetValue(v1.GlobalId);
                double s2 = scalarField.GetValue(v2.GlobalId);
                double s3 = scalarField.GetValue(v3.GlobalId);

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
        /// Calculates the divergence of a piecewise constant vector field defined per element
        /// by integrating the fluxes over the edges (discrete Gauss theorem).
        /// </summary>
        /// <remarks>
        /// This operator is used in the TV gradient (Eq. (A.6)) to assemble
        /// ∇·(∇γ / ||∇γ||) directly on the conductivity elements.
        /// </remarks>
        public static Dictionary<int, double> CalculateElementWiseDivergence(FEMMesh femMesh, VectorField elementField)
        {
            var divergence = new Dictionary<int, double>();
            var elements = femMesh.GetElements().Cast<FEMElement>().ToList();
            var edgeToElements = BuildEdgeToElementMap(elements);

            foreach (var element in elements)
            {
                double centroidX = element.Vertices.Average(v => v.X);
                double centroidY = element.Vertices.Average(v => v.Y);
                double elementFlux = 0.0;

                for (int i = 0; i < element.Vertices.Count; i++)
                {
                    var vStart = element.Vertices[i];
                    var vEnd = element.Vertices[(i + 1) % element.Vertices.Count];

                    double edgeDx = vEnd.X - vStart.X;
                    double edgeDy = vEnd.Y - vStart.Y;
                    double edgeLength = Math.Sqrt(edgeDx * edgeDx + edgeDy * edgeDy);
                    if (edgeLength < 1e-12)
                        continue;

                    // Outward unit normal (rotate edge by +90° and orient away from centroid)
                    double nx = edgeDy;
                    double ny = -edgeDx;
                    double midX = 0.5 * (vStart.X + vEnd.X);
                    double midY = 0.5 * (vStart.Y + vEnd.Y);
                    double toCentroidX = centroidX - midX;
                    double toCentroidY = centroidY - midY;
                    if (nx * toCentroidX + ny * toCentroidY > 0)
                    {
                        nx = -nx;
                        ny = -ny;
                    }
                    double invLength = 1.0 / edgeLength;
                    nx *= invLength;
                    ny *= invLength;

                    var key = NormaliseEdgeKey(vStart.GlobalId, vEnd.GlobalId);
                    edgeToElements.TryGetValue(key, out var attachedElements);
                    int neighbourId = attachedElements?.FirstOrDefault(id => id != element.Id) ?? -1;

                    var (fx, fy) = elementField.GetVector(element.Id);
                    double flux;
                    if (neighbourId >= 0)
                    {
                        var (fnx, fny) = elementField.GetVector(neighbourId);
                        flux = 0.5 * ((fx + fnx) * nx + (fy + fny) * ny);
                    }
                    else
                    {
                        flux = fx * nx + fy * ny;
                    }

                    elementFlux += flux * edgeLength;
                }

                double area = Math.Max(element.Area, 1e-12);
                divergence[element.Id] = elementFlux / area;
            }

            return divergence;
        }

        /// <summary>
        /// Projects a nodal scalar field (e.g., Δγ at vertices) onto the elements by averaging
        /// the values of the element's vertices. This allows us to express vertex-based
        /// operators in the conductivity space (Eq. (A.4)).
        /// </summary>
        public static Dictionary<int, double> ProjectVertexFieldToElements(FEMMesh femMesh, ScalarField vertexField)
        {
            var projection = new Dictionary<int, double>();

            foreach (var element in femMesh.GetElements().Cast<FEMElement>())
            {
                double sum = 0.0;
                foreach (var v in element.Vertices)
                    sum += vertexField.GetValue(v.GlobalId);

                projection[element.Id] = sum / element.Vertices.Count;
            }

            return projection;
        }

        /// <summary>
        /// Computes the discrete Laplacian of a scalar field using the cotangent formula.
        /// This is a common and robust method for unstructured triangular meshes.
        /// The scalar field must be defined per-FEMVertex.
        /// </summary>
        /// <param name="femMesh">The FEM mesh.</param>
        /// <param name="scalarField">A scalar field defined per-FEMVertex.</param>
        /// <returns>A scalar field representing the Laplacian at each FEMVertex.</returns>
        public static PotentialDistribution CalculateLaplacian(FEMMesh femMesh, ScalarField scalarField)
        {
            var laplacianData = new Dictionary<int, double>();
            var adjacency = BuildAdjacencyMap(femMesh);
            var elements = femMesh.GetElements().Cast<FEMElement>();

            foreach (var FEMVertex in femMesh.Vertices)
            {
                int i = FEMVertex.GlobalId;
                double laplacianValue = 0.0;

                if (!adjacency.ContainsKey(i)) continue;

                foreach (int j in adjacency[i]) // For each neighbor j of FEMVertex i
                {
                    double s_i = scalarField.GetValue(i);
                    double s_j = scalarField.GetValue(j);

                    // Find the two triangles sharing the edge (i, j)
                    var sharedTriangles = elements.Where(e =>
                        e.Vertices[0].GlobalId == i && (e.Vertices[1].GlobalId == j || e.Vertices[2].GlobalId == j) ||
                        e.Vertices[1].GlobalId == i && (e.Vertices[0].GlobalId == j || e.Vertices[2].GlobalId == j) ||
                        e.Vertices[2].GlobalId == i && (e.Vertices[0].GlobalId == j || e.Vertices[1].GlobalId == j)
                    ).ToList();

                    double cotAlpha = 0;
                    double cotBeta = 0;

                    if (sharedTriangles.Count > 0)
                    {
                        var p_k = sharedTriangles[0].Vertices[0].GlobalId != i && sharedTriangles[0].Vertices[0].GlobalId != j ? sharedTriangles[0].Vertices[0] :
                                  sharedTriangles[0].Vertices[1].GlobalId != i && sharedTriangles[0].Vertices[1].GlobalId != j ? sharedTriangles[0].Vertices[1] :
                                  sharedTriangles[0].Vertices[2];
                        cotAlpha = Cotangent(femMesh.Vertices[i], femMesh.Vertices[j], p_k);
                    }
                    if (sharedTriangles.Count > 1)
                    {
                        var p_l = sharedTriangles[1].Vertices[0].GlobalId != i && sharedTriangles[1].Vertices[0].GlobalId != j ? sharedTriangles[1].Vertices[0] :
                                  sharedTriangles[1].Vertices[1].GlobalId != i && sharedTriangles[1].Vertices[1].GlobalId != j ? sharedTriangles[1].Vertices[1] :
                                  sharedTriangles[1].Vertices[2];
                        cotBeta = Cotangent(femMesh.Vertices[i], femMesh.Vertices[j], p_l);
                    }

                    laplacianValue += (cotAlpha + cotBeta) * (s_j - s_i);
                }
                laplacianData[i] = 0.5 * laplacianValue;
            }

            return new PotentialDistribution(laplacianData);
        }

        #region Private Helpers

        /// <summary>
        /// Computes the cotangent of the angle at <paramref name="p3"/> for the
        /// triangle defined by <paramref name="p1"/>-<paramref name="p2"/>-<paramref name="p3"/>.
        /// </summary>
        private static double Cotangent(FEMVertex p1, FEMVertex p2, FEMVertex p3)
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

        /// <summary>
        /// Builds an undirected vertex adjacency map for the supplied mesh.
        /// </summary>
        private static Dictionary<int, List<int>> BuildAdjacencyMap(FEMMesh mesh)
        {
            var adjacency = new Dictionary<int, List<int>>();
            var elements = mesh.GetElements().Cast<FEMElement>();

            foreach (var element in elements)
            {
                AddEdge(adjacency, element.Vertices[0].GlobalId, element.Vertices[1].GlobalId);
                AddEdge(adjacency, element.Vertices[1].GlobalId, element.Vertices[2].GlobalId);
                AddEdge(adjacency, element.Vertices[2].GlobalId, element.Vertices[0].GlobalId);
            }
            return adjacency;
        }

        /// <summary>
        /// Inserts an undirected edge into the adjacency map.
        /// </summary>
        private static void AddEdge(Dictionary<int, List<int>> adjacency, int u, int v)
        {
            if (!adjacency.ContainsKey(u)) adjacency[u] = [];
            if (!adjacency.ContainsKey(v)) adjacency[v] = [];
            if (!adjacency[u].Contains(v)) adjacency[u].Add(v);
            if (!adjacency[v].Contains(u)) adjacency[v].Add(u);
        }

        /// <summary>
        /// Builds a mapping from undirected edges to the list of elements that
        /// share the edge.
        /// </summary>
        private static Dictionary<(int, int), List<int>> BuildEdgeToElementMap(IEnumerable<FEMElement> elements)
        {
            var map = new Dictionary<(int, int), List<int>>();
            foreach (var element in elements)
            {
                for (int i = 0; i < element.Vertices.Count; i++)
                {
                    int idA = element.Vertices[i].GlobalId;
                    int idB = element.Vertices[(i + 1) % element.Vertices.Count].GlobalId;
                    var key = NormaliseEdgeKey(idA, idB);
                    if (!map.TryGetValue(key, out var list))
                    {
                        list = new List<int>();
                        map[key] = list;
                    }
                    if (!list.Contains(element.Id))
                        list.Add(element.Id);
                }
            }
            return map;
        }

        /// <summary>
        /// Returns an ordered key for an undirected edge.
        /// </summary>
        private static (int, int) NormaliseEdgeKey(int a, int b) => a < b ? (a, b) : (b, a);

        #endregion
    }
}
