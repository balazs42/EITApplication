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
        public static VectorField CalculateElementWiseGradient(FEMMesh femMesh, ScalarField scalarField, bool useParallel = false)
        {
            var elements = femMesh.ElementsTyped;
            if (useParallel && elements.Count > 1)
            {
                var entries = new KeyValuePair<int, (double X, double Y)>[elements.Count];
                Parallel.For(0, elements.Count, index =>
                {
                    var element = elements[index];
                    entries[index] = new KeyValuePair<int, (double X, double Y)>(
                        element.Id,
                        ComputeElementGradient(element, scalarField));
                });

                var gradientData = new Dictionary<int, (double X, double Y)>(elements.Count);
                for (int i = 0; i < entries.Length; i++)
                    gradientData[entries[i].Key] = entries[i].Value;

                return new VectorField(gradientData);
            }

            return new VectorField(
                elements.ToDictionary(
                    element => element.Id,
                    element => ComputeElementGradient(element, scalarField)));
        }

        /// <summary>
        /// Calculates the divergence of a piecewise constant vector field defined per element
        /// by integrating the fluxes over the edges (discrete Gauss theorem).
        /// </summary>
        /// <remarks>
        /// This operator is used in the TV gradient (Eq. (A.6)) to assemble
        /// ∇·(∇γ / ||∇γ||) directly on the conductivity elements.
        /// </remarks>
        public static Dictionary<int, double> CalculateElementWiseDivergence(FEMMesh femMesh, VectorField elementField, bool useParallel = false)
        {
            var elements = femMesh.ElementsTyped;
            var edgeToElements = BuildEdgeToElementMap(elements);

            if (useParallel && elements.Count > 1)
            {
                var entries = new KeyValuePair<int, double>[elements.Count];
                Parallel.For(0, elements.Count, index =>
                {
                    var element = elements[index];
                    entries[index] = new KeyValuePair<int, double>(element.Id, ComputeElementDivergence(element, elementField, edgeToElements));
                });

                var divergence = new Dictionary<int, double>(elements.Count);
                for (int i = 0; i < entries.Length; i++)
                    divergence[entries[i].Key] = entries[i].Value;

                return divergence;
            }

            var serialDivergence = new Dictionary<int, double>(elements.Count);
            for (int i = 0; i < elements.Count; i++)
            {
                var element = elements[i];
                serialDivergence[element.Id] = ComputeElementDivergence(element, elementField, edgeToElements);
            }

            return serialDivergence;
        }

        /// <summary>
        /// Projects a nodal scalar field (e.g., Δγ at vertices) onto the elements by averaging
        /// the values of the element's vertices. This allows us to express vertex-based
        /// operators in the conductivity space (Eq. (A.4)).
        /// </summary>
        public static Dictionary<int, double> ProjectVertexFieldToElements(FEMMesh femMesh, ScalarField vertexField, bool useParallel = false)
        {
            var elements = femMesh.ElementsTyped;

            if (useParallel && elements.Count > 1)
            {
                var entries = new KeyValuePair<int, double>[elements.Count];
                Parallel.For(0, elements.Count, i =>
                {
                    var element = elements[i];
                    double sum = 0.0;
                    foreach (var v in element.Vertices)
                        sum += vertexField.GetValue(v.GlobalId);

                    entries[i] = new KeyValuePair<int, double>(element.Id, sum / element.Vertices.Count);
                });

                var projection = new Dictionary<int, double>(elements.Count);
                for (int i = 0; i < entries.Length; i++)
                    projection[entries[i].Key] = entries[i].Value;

                return projection;
            }

            var serialProjection = new Dictionary<int, double>(elements.Count);
            for (int i = 0; i < elements.Count; i++)
            {
                var element = elements[i];
                double sum = 0.0;
                foreach (var v in element.Vertices)
                    sum += vertexField.GetValue(v.GlobalId);

                serialProjection[element.Id] = sum / element.Vertices.Count;
            }

            return serialProjection;
        }

        /// <summary>
        /// Computes the discrete Laplacian of a scalar field using cotangent edge weights.
        /// </summary>
        public static PotentialDistribution CalculateLaplacian(FEMMesh femMesh, ScalarField scalarField, bool useParallel = false)
        {
            var elements = femMesh.ElementsTyped;
            var edgeWeights = BuildCotangentEdgeWeights(elements, useParallel);
            var laplacianData = new Dictionary<int, double>(femMesh.Vertices.Count);

            foreach (var vertex in femMesh.Vertices)
                laplacianData[vertex.GlobalId] = 0.0;

            foreach (var kv in edgeWeights)
            {
                UnpackEdgeKey(kv.Key, out int a, out int b);

                double sA = scalarField.GetValue(a);
                double sB = scalarField.GetValue(b);
                double weightedDelta = 0.5 * kv.Value * (sB - sA);

                laplacianData[a] += weightedDelta;
                laplacianData[b] -= weightedDelta;
            }

            return new PotentialDistribution(laplacianData);
        }

        #region Private Helpers

        private static (double X, double Y) ComputeElementGradient(FEMElement element, ScalarField scalarField)
        {
            var v1 = element.Vertices[0];
            var v2 = element.Vertices[1];
            var v3 = element.Vertices[2];

            double s1 = scalarField.GetValue(v1.GlobalId);
            double s2 = scalarField.GetValue(v2.GlobalId);
            double s3 = scalarField.GetValue(v3.GlobalId);

            double twoA = 2 * element.Area;
            if (Math.Abs(twoA) < 1e-12)
                return (0.0, 0.0);

            double dN1dx = (v2.Y - v3.Y) / twoA;
            double dN1dy = (v3.X - v2.X) / twoA;
            double dN2dx = (v3.Y - v1.Y) / twoA;
            double dN2dy = (v1.X - v3.X) / twoA;
            double dN3dx = (v1.Y - v2.Y) / twoA;
            double dN3dy = (v2.X - v1.X) / twoA;

            double gx = s1 * dN1dx + s2 * dN2dx + s3 * dN3dx;
            double gy = s1 * dN1dy + s2 * dN2dy + s3 * dN3dy;
            return (gx, gy);
        }

        private static double ComputeElementDivergence(FEMElement element,
                                                       VectorField elementField,
                                                       Dictionary<(int, int), List<int>> edgeToElements)
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
            return elementFlux / area;
        }

        private static Dictionary<long, double> BuildCotangentEdgeWeights(IReadOnlyList<FEMElement> elements, bool useParallel)
        {
            int triangleCount = elements.Count;
            int estimatedEdgeCount = Math.Max(triangleCount * 3 / 2, 16);

            if (useParallel && triangleCount > 1)
            {
                var locals = new System.Collections.Concurrent.ConcurrentBag<Dictionary<long, double>>();
                int workerCount = Math.Min(Environment.ProcessorCount, triangleCount);
                int localCapacity = Math.Max(estimatedEdgeCount / Math.Max(workerCount, 1), 32);

                Parallel.ForEach(System.Collections.Concurrent.Partitioner.Create(0, triangleCount),
                    new ParallelOptions { MaxDegreeOfParallelism = workerCount },
                    () => new Dictionary<long, double>(localCapacity),
                    (range, _, local) =>
                    {
                        for (int i = range.Item1; i < range.Item2; i++)
                            AccumulateCotangentWeights(elements[i], local);
                        return local;
                    },
                    local => locals.Add(local));

                var merged = new Dictionary<long, double>(estimatedEdgeCount);
                foreach (var local in locals)
                {
                    foreach (var kv in local)
                    {
                        if (merged.TryGetValue(kv.Key, out double existing))
                            merged[kv.Key] = existing + kv.Value;
                        else
                            merged[kv.Key] = kv.Value;
                    }
                }

                return merged;
            }

            var edgeWeights = new Dictionary<long, double>(estimatedEdgeCount);
            for (int i = 0; i < triangleCount; i++)
                AccumulateCotangentWeights(elements[i], edgeWeights);

            return edgeWeights;
        }

        private static void AccumulateCotangentWeights(FEMElement element, Dictionary<long, double> edgeWeights)
        {
            var a = element.Vertices[0];
            var b = element.Vertices[1];
            var c = element.Vertices[2];

            AddEdgeWeight(edgeWeights, b.GlobalId, c.GlobalId, Cotangent(b, c, a));
            AddEdgeWeight(edgeWeights, c.GlobalId, a.GlobalId, Cotangent(c, a, b));
            AddEdgeWeight(edgeWeights, a.GlobalId, b.GlobalId, Cotangent(a, b, c));
        }

        private static void AddEdgeWeight(Dictionary<long, double> edgeWeights, int first, int second, double weight)
        {
            long key = PackEdgeKey(first, second);
            if (edgeWeights.TryGetValue(key, out double existing))
                edgeWeights[key] = existing + weight;
            else
                edgeWeights[key] = weight;
        }

        private static long PackEdgeKey(int first, int second)
        {
            if (first > second)
                (first, second) = (second, first);

            return ((long)first << 32) | (uint)second;
        }

        private static void UnpackEdgeKey(long key, out int first, out int second)
        {
            first = (int)(key >> 32);
            second = (int)(key & 0xFFFFFFFF);
        }

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
