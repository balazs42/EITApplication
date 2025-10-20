using Utility.Classes.Discretizer.FiniteElementMesh;
using Utility.Classes.Discretizer.GraphMesh;
using Utility.Classes.ReconstructionParameters;

namespace Utility.Classes.Solvers.GraphBasedSolver
{

    /// <summary>
    /// Operators that act directly on a Graph (from FEMMesh.ToGraph()).
    /// Builds K (Laplacian) from graph edge weights, stamps CEM shunts,
    /// maps FEM electrodes to graph boundary nodes by nearest (x,y).
    /// </summary>
    public class GraphBasedOperators
    {
        private readonly INumericSolver _solver;
        private readonly Graph _graph;
        private readonly Dictionary<int, int> _vidx;   // graph GlobalId -> 0..N-1

        /// <summary>
        /// Creates a new operator suite bound to the provided graph and
        /// numeric solver.
        /// </summary>
        /// <param name="graph">Graph representation of the domain.</param>
        /// <param name="solver">Linear solver used for CEM systems.</param>
        public GraphBasedOperators(Graph graph, INumericSolver solver)
        {
            _graph = graph ?? throw new ArgumentNullException(nameof(graph));
            _solver = solver ?? throw new ArgumentNullException(nameof(solver));

            _vidx = new Dictionary<int, int>(graph.Vertices.Count);
            for (int i = 0; i < graph.Vertices.Count; i++)
                _vidx[graph.Vertices[i].GlobalId] = i;
        }

        public int NodeCount => _graph.NodeCount;
        public int EdgeCount => _graph.EdgeCount;

        /// <summary>
        /// Assembles the weighted graph Laplacian using the supplied edge
        /// conductances.
        /// </summary>
        private double[,] BuildLaplacian(double[] w)
        {
            int N = _graph.NodeCount;
            var K = new double[N, N];

            for (int e = 0; e < _graph.Edges.Count; e++)
            {
                var ge = _graph.Edges[e];
                int i = _vidx[ge.Vertices[0].GlobalId];
                int j = _vidx[ge.Vertices[1].GlobalId];
                double we = Math.Max(w[e], 0.0);
                K[i, i] += we; K[j, j] += we;
                K[i, j] -= we; K[j, i] -= we;
            }
            return K;
        }

        /// <summary>
        /// Selects a grounded electrode, falling back to the first electrode
        /// when none is explicitly marked.
        /// </summary>
        private static int PickGround(IReadOnlyList<FEMElectrode> el)
        {
            int g = el.ToList().FindIndex(e => e.IsGround);
            return g >= 0 ? g : 0;
        }

        /// <summary>
        /// Maps each FEM electrode to the nearest boundary nodes in the graph
        /// domain so that electrode potentials can be imposed.
        /// </summary>
        private Dictionary<int, List<int>> MapElectrodesToGraphNodes(FEMMesh mesh)
        {
            var map = new Dictionary<int, List<int>>();

            var boundary = _graph.Vertices
                                 .Select((v, i) => (v, i))
                                 .Where(t => t.v.BoundaryId != 0)
                                 .Select(t => t.i)
                                 .ToList();
            if (boundary.Count == 0) boundary = [.. Enumerable.Range(0, _graph.Vertices.Count)];

            (double x, double y) VPos(int id)
            {
                var v = mesh.Vertices.FirstOrDefault(p => p.GlobalId == id) ?? mesh.Vertices[id];
                return (v.X, v.Y);
            }

            int Nearest((double x, double y) p)
            {
                double best = double.MaxValue; int bestIdx = 0;
                foreach (var i in boundary)
                {
                    double dx = _graph.Vertices[i].X - p.x, dy = _graph.Vertices[i].Y - p.y;
                    double d2 = dx * dx + dy * dy;
                    if (d2 < best) { best = d2; bestIdx = i; }
                }
                return bestIdx;
            }

            var electrodes = mesh.GetElectrodes().Cast<FEMElectrode>().ToList();
            for (int ell = 0; ell < electrodes.Count; ell++)
            {
                var e = electrodes[ell];
                var set = new HashSet<int>();

                if (!e.PointElectrode && e.FEMVertexIds != null && e.FEMVertexIds.Count > 0)
                    foreach (var vid in e.FEMVertexIds) set.Add(Nearest(VPos(vid)));
                else
                    set.Add(Nearest(VPos(e.MeshId)));

                map[ell] = [.. set];
            }
            return map;
        }

        /// <summary>
        /// Builds the graph-based CEM matrices for a given set of edge weights
        /// and returns the electrode-to-node mapping used during assembly.
        /// </summary>
        private void AssembleCEM(double[] w, FEMMesh mesh,
                                 out double[,] Kt, out double[,] A, out double[,] D,
                                 out int ground, out Dictionary<int, List<int>> emap)
        {
            var K = BuildLaplacian(w);
            var electrodes = mesh.GetElectrodes().Cast<FEMElectrode>().ToList();

            int N = _graph.NodeCount, L = electrodes.Count;
            A = new double[N, L];
            D = new double[L, L];
            emap = MapElectrodesToGraphNodes(mesh);

            for (int ell = 0; ell < L; ell++)
            {
                var el = electrodes[ell];
                double beta = el.ZContact > 0.0 ? 1.0 / el.ZContact : 1e12;

                foreach (var b in emap[ell])
                {
                    K[b, b] += beta;
                    A[b, ell] += beta;
                    D[ell, ell] += beta;
                }
            }
            Kt = K;
            ground = PickGround(electrodes);
        }

        /// <summary>
        /// Solves the graph-based CEM system for a given edge-weight vector,
        /// returning both nodal potentials and electrode voltages.
        /// </summary>
        public (double[] phi, double[] U, Dictionary<int, List<int>> map) SolveCEM(double[] w, FEMMesh mesh)
        {
            AssembleCEM(w, mesh, out var Kt, out var A, out var D, out int g, out var map);

            int N = _graph.NodeCount;
            var electrodes = mesh.GetElectrodes().Cast<FEMElectrode>().ToList();
            int L = electrodes.Count, Lr = L - 1, nTot = N + Lr;

            var S = new double[nTot, nTot];
            var rhs = new double[nTot];

            for (int i = 0; i < N; i++)
                for (int j = 0; j < N; j++)
                    S[i, j] = Kt[i, j];

            int ColU(int ell) => ell < g ? N + ell : N + ell - 1;

            for (int b = 0; b < N; b++)
                for (int ell = 0; ell < L; ell++)
                    if (ell != g) S[b, ColU(ell)] = -A[b, ell];

            for (int ell = 0; ell < L; ell++)
            {
                if (ell == g) continue;
                int irow = ColU(ell);
                for (int b = 0; b < N; b++) S[irow, b] = -A[b, ell];
                for (int m = 0; m < L; m++)
                    if (m != g) S[irow, ColU(m)] = ell == m ? D[ell, ell] : 0.0;
            }

            for (int ell = 0; ell < L; ell++)
                if (ell != g) rhs[ColU(ell)] = electrodes[ell].Current;

            var x = _solver.SolveLinearSystem(S, rhs);

            var phi = new double[N];
            Array.Copy(x, 0, phi, 0, N);

            var U = new double[L];
            for (int ell = 0; ell < L; ell++) U[ell] = ell == g ? 0.0 : x[ColU(ell)];

            return (phi, U, map);
        }

        /// <summary>
        /// Computes the grounded electrode response matrix Λ that maps drive
        /// currents to electrode voltages on the graph model.
        /// </summary>
        public double[,] ElectrodeResponse(double[] w, FEMMesh mesh)
        {
            var electrodes = mesh.GetElectrodes().Cast<FEMElectrode>().ToList();
            int L = electrodes.Count;
            if (L == 0) return new double[0, 0];

            var resp = new double[L, L];
            var saved = electrodes.Select(e => e.Current).ToArray();

            for (int ell = 0; ell < L; ell++)
            {
                for (int k = 0; k < L; k++) electrodes[k].Current = 0.0;
                electrodes[ell].Current = +1.0;

                var (_, U, _) = SolveCEM(w, mesh);
                for (int r = 0; r < L; r++) resp[r, ell] = U[r];
            }
            for (int k = 0; k < L; k++) electrodes[k].Current = saved[k];

            return resp;
        }
    }
}
