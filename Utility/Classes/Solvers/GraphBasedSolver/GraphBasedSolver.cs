using Utility.Classes.Discretizer.FiniteElementMesh;
using Utility.Classes.Discretizer.GraphMesh;
using Utility.Classes.ReconstructionParameters;

namespace Utility.Classes.Solvers.GraphBasedSolver
{
    /// <summary>
    /// Solver that uses only FEMMesh.ToGraph() / FEMMesh.FromGraph().
    ///  mesh --ToGraph--> graph --CEM solve--> write φ to graph --FromGraph--> FEM potentials / conductivities.
    /// </summary>
    public class GraphBasedSolver
    {
        private readonly INumericSolver _numericSolver;

        private Graph _graph;
        private GraphBasedOperators _ops;
        private Dictionary<int, int> _vidx;  // graph GlobalId -> 0..N-1

        // edge parameters: w = α ∘ w̄
        private double[] _wbar;
        private double[] _alpha;

        public double[] LatestElectrodePotentials { get; set; } = Array.Empty<double>();

        public GraphBasedSolver(FEMMesh mesh, INumericSolver solver, double lambdaW, double lambdaAlpha,
            double stepW, double stepAlpha, double epsilon)
        {
            _numericSolver = solver ?? throw new ArgumentNullException(nameof(solver));

            // Build working graph from the mesh
            _graph = mesh.ToGraph();
            _vidx = new Dictionary<int, int>(_graph.NodeCount);
            for (int k = 0; k < _graph.Vertices.Count; k++)
                _vidx[_graph.Vertices[k].GlobalId] = k;

            // Initialize parameters from graph edge weights
            _wbar = _graph.Edges.Select(e => Math.Max(e.Weight, 1e-12)).ToArray();
            _alpha = Enumerable.Repeat(1.0, _graph.EdgeCount).ToArray();

            _ops = new GraphBasedOperators(_graph, _numericSolver);
        }

        public GraphBasedOperators GetOperators() => _ops;
        public Graph GetGraph() => _graph;

        public double[] CurrentEdgeWeights()
        {
            var w = new double[_graph.EdgeCount];
            for (int e = 0; e < w.Length; e++) w[e] = _alpha[e] * _wbar[e];
            return w;
        }

        /// <summary>
        /// Forward: solve CEM on the graph, write φ to graph, rebuild a FEM mesh and return its potentials.
        /// </summary>
        public PotentialDistribution SolveForward(FEMMesh mesh, INumericSolver _)
        {
            var (phi, U, _) = _ops.SolveCEM(CurrentEdgeWeights(), mesh);
            LatestElectrodePotentials = U;

            for (int i = 0; i < _graph.Vertices.Count; i++)
                _graph.Vertices[i].Potential = phi[i];

            var newMesh = mesh.FromGraph(_graph) as FEMMesh;
            return newMesh!.GetPotentialDistribution();
        }

        /// <summary>
        /// Grounded electrode response matrix Λ (currents -> electrode voltages).
        /// </summary>
        public double[,] ComputeElectrodeResponse(FEMMesh mesh)
        {
            return _ops.ElectrodeResponse(CurrentEdgeWeights(), mesh);
        }

        /// <summary>
        /// Minimal α-update consistent with adjoint structure on the graph, then push back a conductivity field.
        /// </summary>
        public ConductivityDistribution Iteration(FEMMesh mesh, INumericSolver _)
        {
            // Forward
            var pd = SolveForward(mesh, _numericSolver);
            var Upred = LatestElectrodePotentials;
            var electrodes = mesh.GetElectrodes().Cast<FEMElectrode>().ToList();

            // Electrode residuals
            var r = new double[Upred.Length];
            for (int i = 0; i < r.Length; i++)
                r[i] = Upred[i] - electrodes[i].Potential;

            // Lift residuals to graph nodes (use same electrode->node map from CEM assembly)
            var (_, _, emap) = _ops.SolveCEM(CurrentEdgeWeights(), mesh);
            var nodeAdj = new double[_graph.NodeCount];
            foreach (var kv in emap)
            {
                double val = r[kv.Key];
                foreach (var b in kv.Value) nodeAdj[b] += val / Math.Max(1, kv.Value.Count);
            }

            // Gradient on α via (φ_i-φ_j)(μ_i-μ_j)
            var phi = _graph.Vertices.Select(v => v.Potential).ToArray();
            var gradAlpha = new double[_graph.EdgeCount];

            for (int e = 0; e < _graph.EdgeCount; e++)
            {
                var ge = _graph.Edges[e];
                int i = _vidx[ge.Vertices[0].GlobalId];
                int j = _vidx[ge.Vertices[1].GlobalId];

                double dphi = phi[i] - phi[j];
                double dmu = nodeAdj[i] - nodeAdj[j];
                gradAlpha[e] = _wbar[e] * dphi * dmu;
            }

            // Small safe step, keep α ∈ (ε,1]
            const double step = 1e-3, eps = 1e-6;
            for (int e = 0; e < _alpha.Length; e++)
                _alpha[e] = Math.Max(eps, Math.Min(1.0, _alpha[e] - step * gradAlpha[e]));

            // Update graph edge weights
            for (int e = 0; e < _graph.EdgeCount; e++)
                _graph.Edges[e].Weight = _alpha[e] * _wbar[e];

            // Push back to a FEM conductivity field by rebuilding from the updated graph
            var newMesh = mesh.FromGraph(_graph);

            return newMesh.GetConductivityDistribution();
        }
    }
}