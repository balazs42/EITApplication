using Utility.Classes.Discretizer.GraphMesh;

namespace Utility.Classes.Solvers.GraphBasedSolver
{
    /// <summary>
    ///     Implements the Curtis–Ingerman–Morrow (CIM) layer peeling
    ///     algorithm for recovering edge conductances of a critical circular
    ///     planar graph from a measured Dirichlet-to-Neumann (response)
    ///     matrix.  The workflow follows the classical three step procedure
    ///     described in [Curtis, Ingerman, Morrow 1998]:
    ///
    ///     <list type="number">
    ///         <item>Project the measured matrix onto the gauge manifold –
    ///         symmetrising and enforcing zero row sums.</item>
    ///         <item>Peel boundary vertices one at a time.  The conductances
    ///         of edges incident to the peeled vertex are recovered from the
    ///         corresponding row of the response matrix.</item>
    ///         <item>Update the remaining response matrix using a Schur
    ///         complement and repeat until all vertices are removed.</item>
    ///     </list>
    ///
    ///     The implementation below is intentionally lightweight; it assumes
    ///     that the supplied graph is critical and circular planar so that at
    ///     every peeling step the boundary vertex has at most one interior
    ///     neighbour.  Nevertheless the routine provides a clear reference
    ///     implementation of the CIM workflow that can be replaced by a more
    ///     sophisticated version if required.
    /// </summary>
    public class CimInverter
    {
        /// <summary>
        ///     Recover edge conductances from a measured Dirichlet-to-Neumann
        ///     matrix assuming the graph is critical and circular planar.
        ///     The algorithm symmetrises the measured matrix, enforces the
        ///     necessary gauge constraints and then performs the layer peeling
        ///     procedure.
        /// </summary>
        /// <param name="measured">Measured boundary response matrix Λ.</param>
        /// <param name="graph">Graph whose edge conductances are sought.</param>
        /// <returns>Array of conductances in the same ordering as graph.Edges.</returns>
        public double[] Invert(double[,] measured, Graph graph)
        {
            if (measured == null) throw new ArgumentNullException(nameof(measured));
            if (graph == null) throw new ArgumentNullException(nameof(graph));

            // Extract boundary vertex indices in the graph.  The measured
            // matrix is assumed to follow the same ordering.
            var boundary = graph.Vertices
                                .Select((v, idx) => (v, idx))
                                .Where(t => t.v.BoundaryId != 0)
                                .Select(t => t.idx)
                                .ToList();

            if (measured.GetLength(0) != measured.GetLength(1))
                throw new ArgumentException("Response matrix must be square.", nameof(measured));

            if (boundary.Count != measured.GetLength(0))
                throw new ArgumentException("Boundary vertex count and response matrix size mismatch.");

            // Map ordered pairs of vertices to edge indices for quick lookup
            var edgeIndex = new Dictionary<(int, int), int>();
            for (int e = 0; e < graph.Edges.Count; e++)
            {
                int i = graph.Vertices.IndexOf(graph.Edges[e].Vertices[0]);
                int j = graph.Vertices.IndexOf(graph.Edges[e].Vertices[1]);
                edgeIndex[(i, j)] = e;
                edgeIndex[(j, i)] = e;
            }

            // Adjacency lists of the graph that we will gradually peel.
            var adj = new Dictionary<int, List<int>>();
            for (int i = 0; i < graph.Vertices.Count; i++)
                adj[i] = new List<int>();
            foreach (var e in graph.Edges)
            {
                int i = graph.Vertices.IndexOf(e.Vertices[0]);
                int j = graph.Vertices.IndexOf(e.Vertices[1]);
                adj[i].Add(j); adj[j].Add(i);
            }

            // Prepare the working response matrix.
            var lambda = ProjectToGauge(measured);
            var conductances = new double[graph.EdgeCount];

            // Boundary vertex list associated with the rows/columns of λ.
            var boundaryOrder = boundary.ToList();

            while (boundaryOrder.Count > 0)
            {
                // Peel the first boundary vertex in the list.
                int bIndex = 0;
                int v = boundaryOrder[bIndex];

                var neighbours = adj.ContainsKey(v) ? adj[v] : new List<int>();
                double diag = lambda[bIndex, bIndex];
                double accounted = 0.0;

                // Edges to other boundary vertices are read directly from the
                // off–diagonal entries of Λ.
                foreach (var nb in neighbours)
                {
                    int nbIdx = boundaryOrder.IndexOf(nb);
                    if (nbIdx >= 0)
                    {
                        double g = -lambda[bIndex, nbIdx];
                        g = Math.Max(g, 0.0);
                        conductances[edgeIndex[(v, nb)]] = g;
                        accounted += g;
                    }
                }

                // Any remaining incident edge must connect to an interior
                // vertex.  Under the critical graph assumption there can be at
                // most one such edge; its conductance is obtained from the
                // diagonal entry once all boundary contributions are known.
                foreach (var nb in neighbours)
                {
                    int nbIdx = boundaryOrder.IndexOf(nb);
                    if (nbIdx < 0)
                    {
                        double g = diag - accounted;
                        g = Math.Max(g, 0.0);
                        conductances[edgeIndex[(v, nb)]] = g;
                    }
                }

                // Update adjacency and boundary set
                foreach (var nb in neighbours)
                    adj[nb].Remove(v);
                adj.Remove(v);

                // Schur complement to update Λ after removing vertex v
                lambda = SchurComplement(lambda, bIndex);
                boundaryOrder.RemoveAt(bIndex);

                // Newly exposed neighbours become boundary vertices
                foreach (var nb in neighbours)
                    if (adj.ContainsKey(nb) && !boundaryOrder.Contains(nb))
                        boundaryOrder.Add(nb);
            }

            // Replace any unresolved conductances with a tiny positive value.
            for (int e = 0; e < conductances.Length; e++)
                conductances[e] = conductances[e] > 0.0 ? conductances[e] : 1e-12;

            return conductances;
        }

        /// <summary>
        ///     Symmetrise the response matrix and enforce zero row sums in
        ///     order to project onto the admissible manifold of Dirichlet-to-
        ///     Neumann maps.  This removes small numerical asymmetries and
        ///     makes the subsequent peeling stable.
        /// </summary>
        private static double[,] ProjectToGauge(double[,] L)
        {
            int n = L.GetLength(0);
            var R = new double[n, n];

            for (int i = 0; i < n; i++)
                for (int j = 0; j < n; j++)
                    R[i, j] = 0.5 * (L[i, j] + L[j, i]);

            for (int i = 0; i < n; i++)
            {
                double row = 0.0;
                for (int j = 0; j < n; j++) row += R[i, j];
                R[i, i] -= row;
            }

            return R;
        }

        /// <summary>
        ///     Compute the Schur complement of <paramref name="L"/> with
        ///     respect to the row/column <paramref name="idx"/>.  This
        ///     updates the response matrix after peeling a boundary vertex.
        /// </summary>
        private static double[,] SchurComplement(double[,] L, int idx)
        {
            int n = L.GetLength(0);
            if (n <= 1) return new double[0, 0];

            int m = n - 1;
            var R = new double[m, m];
            double pivot = L[idx, idx];

            int ii = 0;
            for (int i = 0; i < n; i++)
            {
                if (i == idx) continue;
                int jj = 0;
                for (int j = 0; j < n; j++)
                {
                    if (j == idx) continue;
                    R[ii, jj] = L[i, j] - L[i, idx] * L[idx, j] / pivot;
                    jj++;
                }
                ii++;
            }
            return R;
        }
    }
}
