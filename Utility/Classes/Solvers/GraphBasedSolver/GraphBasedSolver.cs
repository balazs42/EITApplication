using Utility.Classes.Meshing.FiniteElementMesh;
using Utility.Classes.ReconstructionParameters;

namespace Utility.Classes.Solvers.GraphBasedSolver
{
    public class GraphBasedSolver
    {
        private readonly GraphAssembler _assembler;
        private readonly GraphBasedOperators _lapSolver;
        private readonly AdjointGradient _adjGrad;
        private readonly INumericSolver _numericSolver;


        // current parameters
        private double[] _wbar;
        private double[] _alpha;

        // user‐tunable hyperparameters
        private readonly double _stepW;
        private readonly double _stepAlpha;
        private readonly double _epsilon;

        /// <summary>
        /// Ctor: build graph, initialize solver & gradient, set steps.
        /// </summary>
        public GraphBasedSolver(FEMMesh mesh, INumericSolver solver, double lambdaW, double lambdaAlpha, double stepW, double stepAlpha, double epsilon)
        {
            _numericSolver = solver;

            // 1) Extract graph from FEM mesh
            _assembler = new GraphAssembler();
            _assembler.Build(mesh);

            // 2) Initialize w̄ and α
            _wbar = (double[])_assembler.Wbar.Clone();
            _alpha = (double[])_assembler.Alpha.Clone();

            // 3) Set up forward solver and adjoint
            _lapSolver = new GraphBasedOperators(_assembler, solver);
            _adjGrad = new AdjointGradient(_lapSolver, lambdaW, lambdaAlpha, epsilon);

            // 4) Learning rates and minimum alpha
            _stepW = stepW;
            _stepAlpha = stepAlpha;
            _epsilon = epsilon;
        }

        public PotentialDistribution SolveForward(FEMMesh mesh, INumericSolver numericSolver)
        {
            int N = _assembler.NodeCount;
            int E = _assembler.Edges.Count;

            // 1) Build current edge weights w = α·w̄
            var w = new double[E];
            for (int e = 0; e < E; e++)
                w[e] = _alpha[e] * _wbar[e];

            // 2) Build RHS currents I from electrodes in mesh
            var electrodes = mesh.GetElectrodes().Cast<FEMElectrode>();
            var I = new double[N];
            foreach (var el in electrodes)
                I[el.MeshId] = el.Current;

            // 3) Forward solve for φ
            var phi = _lapSolver.SolveLaplacian(w, I);

            Dictionary<int, double> pd = new();
            for(int i = 0; i < phi.Length; i++)
                pd.Add(i, phi[i]);

            return new PotentialDistribution(pd);
        }

        public PotentialDistribution SolveAdjoint(FEMMesh mesh, INumericSolver numericSolver)
        {
            // 1) Number of graph nodes (same as FEM vertices)
            int N = _assembler.NodeCount;

            // 2) Re–build the current edge weights w_e = α_e * w̄_e
            int E = _assembler.Edges.Count;
            var w = new double[E];
            for (int e = 0; e < E; e++)
                w[e] = _alpha[e] * _wbar[e];

            // 3) Forward solve to get φ at each node
            var forwardPD = SolveForward(mesh, _numericSolver);
            //    Extract φ_i from the PotentialDistribution
            var phiDict = forwardPD.Potentials;  // Dictionary<nodeID,double>

            // 4) Build the residual r_i = φ_i - U_obs at electrode nodes
            //    Initialize all entries to zero
            var r = new double[N];
            var electrodes = mesh.GetElectrodes().Cast<FEMElectrode>();

            foreach (var el in electrodes)
            {
                // el.MeshId is the graph‐node index of this electrode
                double phiVal = phiDict[el.MeshId];
                double uObs = el.Potential;
                r[el.MeshId] = phiVal - uObs;
            }

            // 5) Solve the adjoint system L(w) μ = r
            //    using the same graph Laplacian solver
            var mu = _lapSolver.SolveLaplacian(w, r);

            // 6) Pack μ into a PotentialDistribution
            var muDict = new Dictionary<int, double>(N);
            for (int i = 0; i < N; i++)
            {
                // mesh.Vertices[i].GlobalId = the FEM‐node ID
                int nodeId = mesh.Vertices[i].GlobalId;
                muDict[nodeId] = mu[i];
            }

            return new PotentialDistribution(muDict);
        }

        /// <summary>
        /// Performs one inversion iteration:
        ///   - forward: L(w) φ = I
        ///   - adjoint: L(w) ψ = Sᵀ(φ - U_obs)
        ///   - gradient update of w̄ and α
        /// </summary>
        public ConductivityDistribution Iteration(FEMMesh mesh, INumericSolver femSolver)
        {
            int N = _assembler.NodeCount;
            int E = _assembler.Edges.Count;

            // 1) Build current edge weights w = α·w̄
            var w = new double[E];
            for (int e = 0; e < E; e++)
                w[e] = _alpha[e] * _wbar[e];

            // 2) Build RHS currents I from electrodes in mesh
            var electrodes = mesh.GetElectrodes().Cast<FEMElectrode>();
            int electrodeCount = electrodes.Count();
            var I = new double[N];

            foreach (var el in electrodes)
                I[el.MeshId] = el.Current;

            // 3) Forward solve for φ
            var phi = _lapSolver.SolveLaplacian(w, I);

            // 4) Collect observed voltages U_obs
            var Uobs = new double[electrodeCount];
            for (int ell = 0; ell < electrodeCount; ell++)
                Uobs[ell] = electrodes.ElementAt(ell).Potential;

            // 5) Compute adjoint gradients
            var (gradWbar, gradAlpha) = _adjGrad.Compute(_wbar, _alpha, phi, Uobs);

            // 6) Gradient descent updates
            for (int e = 0; e < E; e++)
            {
                // update w̄_e
                _wbar[e] -= _stepW * gradWbar[e];
                // update α_e and clamp into [ε,1-ε]
                _alpha[e] = Math.Clamp(_alpha[e] - _stepAlpha * gradAlpha[e], _epsilon, 1.0 - _epsilon);
            }

            return GetConductivityDistribution(mesh);
            // 7) (optionally) inject updated σ back into mesh if needed
            // e.g. mesh.SetConductivityDistribution(...)
        }

        /// <summary>
        /// Convert the graph’s (w̄, α) back into a FEM ConductivityDistribution.
        /// For each FEM element, we look at all graph edges (i,j) that lie on that
        /// element and average their effective conductances w_e = α_e·w̄_e.
        /// </summary>
        public ConductivityDistribution GetConductivityDistribution(FEMMesh mesh)
        {
            var elements = mesh.GetElements().Cast<FEMElement>();
            int elementCount = elements.Count();
            var sigmaDict = new Dictionary<int, double>(elementCount);
            int E = _assembler.Edges.Count;

            // Precompute effective w = α·w̄ for each edge
            double[] we = new double[E];
            for (int e = 0; e < E; e++)
                we[e] = _alpha[e] * _wbar[e];

            // For every element in the FEM mesh:
            foreach (var elem in elements)
            {
                // The set of node‐IDs that this element touches
                var vids = new int[] { elem.Vertices[0].GlobalId, elem.Vertices[1].GlobalId, elem.Vertices[2].GlobalId};
                double sum = 0.0;
                int count = 0;

                // Scan through all graph edges (i,j)
                for (int e = 0; e < E; e++)
                {
                    var (i, j) = _assembler.Edges[e];
                    // If both endpoints belong to this element’s vertices,
                    // treat that edge as part of the element’s local connectivity
                    if (Array.IndexOf(vids, i) >= 0 && Array.IndexOf(vids, j) >= 0)
                    {
                        sum += we[e];
                        count++;
                    }
                }

                // Average (or zero if no matching edges)
                double avg = (count > 0) ? (sum / count) : 0.0;
                sigmaDict[elem.Id] = avg;
            }

            return new ConductivityDistribution(sigmaDict);
        }
    }
}
