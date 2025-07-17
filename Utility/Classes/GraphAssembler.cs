using Utility.Classes.Meshing;

namespace Utility.Classes
{
    /// <summary>
    /// Builds a weighted graph representation (V,E,w̄,α) from an FEM mesh for EIT.
    /// </summary>
    public class GraphAssembler
    {
        /// <summary>
        /// Number of vertices in the graph (mesh nodes).
        /// </summary>
        public int NodeCount { get; private set; }

        /// <summary>
        /// List of undirected edges (i,j) with i<j.
        /// </summary>
        public List<(int i, int j)> Edges { get; private set; }

        /// <summary>
        /// Base conductances w̄_{ij} = ∫γ ∇ϕ_i·∇ϕ_j dΩ for each edge.
        /// </summary>
        public double[] Wbar { get; private set; }

        /// <summary>
        /// Soft topology variables α_{ij}∈[0,1] for each edge.
        /// Initialized to 1.0 (fully connected).
        /// </summary>
        public double[] Alpha { get; private set; }

        public List<FEMElectrode> Electrodes { get; private set; }

        /// <summary>
        /// Builds the graph (V,E) and initializes w̄ and α arrays from the FEM mesh.
        /// </summary>
        public void Build(FEMMesh mesh)
        {
            // 1) Determine total number of nodes
            NodeCount = mesh.Vertices.Count;

            // 2) Collect unique edges from mesh elements
            var edgeSet = new HashSet<(int, int)>();

            // Loop each element in mesh
            foreach (var elem in mesh.Elements)
            {
                // elem.VertexIds gives the node indices for this element (e.g. triangle)
                var vids = new int[] { elem.Vertices[0].GlobalId, elem.Vertices[1].GlobalId, elem.Vertices[2].GlobalId };
                int m = vids.Length;
                // consider each pair of nodes (a,b)
                for (int a = 0; a < m; a++)
                {
                    for (int b = a + 1; b < m; b++)
                    {
                        int i = vids[a], j = vids[b];
                        // store with smaller id first
                        var edge = i < j ? (i, j) : (j, i);
                        edgeSet.Add(edge);
                    }
                }
            }

            // Convert to list for indexing
            Edges = edgeSet.ToList();
            int E = Edges.Count;

            // 3) Allocate arrays for w̄ and α
            Wbar = new double[E];
            Alpha = new double[E];

            // Initialize α to 1.0 (all edges present)
            for (int e = 0; e < E; e++)
                Alpha[e] = 1.0;

            // 4) Compute base conductance w̄_{ij} for each edge
            for (int e = 0; e < E; e++)
            {
                var (i, j) = Edges[e];
                double sum = 0.0;
                // Sum element contributions where both nodes appear
                foreach (var elem in mesh.Elements)
                {
                    var vids = new int[] { elem.Vertices[0].GlobalId, elem.Vertices[1].GlobalId, elem.Vertices[2].GlobalId };
                    // check if element shares this edge
                    if (vids.Contains(i) && vids.Contains(j))
                    {
                        // FEM element method provides conductance between i,j
                        // via S.ElementsConductance(i,j)
                        sum += elem.Conductivity;
                    }
                }
                Wbar[e] = sum;
            }
        }
    }
}
