
namespace Utility.Classes.Solvers
{
    using Utility.Classes.ReconstructionParameters;
    public class GraphBasedOperators
    {
        public readonly INumericSolver _solver;
        public readonly GraphAssembler _graph;
        /// <summary>
        /// Constructs the solver with the assembled graph and a linear solver.
        /// </summary>
        public GraphBasedOperators(GraphAssembler graph, INumericSolver solver)
        {
            _graph = graph;
            _solver = solver;
        }

        /// <summary>
        /// Solve L(w) φ = f for φ, where w_e = α_e * w̄_e.
        /// </summary>
        /// <param name="w">Edge weights after multiplying α·w̄</param>
        /// <param name="f">Right-hand side (injected currents) at nodes</param>
        /// <returns>Node potentials φ of length NodeCount</returns>
        public double[] SolveLaplacian(double[] w, double[] f)
        {
            int N = _graph.NodeCount;
            int E = _graph.Edges.Count;

            // 1) Allocate L as a dense matrix for simplicity
            var L = new double[N, N];

            // 2) Build Laplacian
            for (int e = 0; e < E; e++)
            {
                var (i, j) = _graph.Edges[e];
                double weight = w[e];
                // Degree contributions
                L[i, i] += weight;
                L[j, j] += weight;
                // Off-diagonal
                L[i, j] -= weight;
                L[j, i] -= weight;
            }

            // 3) Solve linear system L φ = f
            // using your FEM numeric solver
            var phi = _solver.SolveLinearSystem(L, f);

            return phi;
        }
    }

    /// <summary>
    /// Computes gradients ∂J/∂w̄ and ∂J/∂α using the adjoint state method.
    /// </summary>
    public class AdjointGradient
    {
        private readonly GraphBasedOperators _lapSolver;
        private readonly double _lambdaW;
        private readonly double _lambdaAlpha;
        private readonly double _epsilon;

        /// <summary>
        /// Constructor: set up adjoint solver and regularization weights.
        /// </summary>
        public AdjointGradient(GraphBasedOperators lapSolver, double lambdaW, double lambdaAlpha, double epsilon)
        {
            _lapSolver = lapSolver;
            _lambdaW = lambdaW;
            _lambdaAlpha = lambdaAlpha;
            _epsilon = epsilon;
        }

        /// <summary>
        /// Compute gradients given current w̄, α, state φ, and observed U_obs.
        /// </summary>
        public (double[] gradWbar, double[] gradAlpha) Compute(double[] wbar, double[] alpha, double[] phi, double[] Uobs)
        {
            int E = wbar.Length;
            int N = _lapSolver._graph.NodeCount;

            // 1) Direct misfit measurement: M = ½||Sφ - Uobs||²
            // residual rell = φ[nodeell] - Uobs[ell]
            var electrodes = _lapSolver._graph.Electrodes;
            int L = electrodes.Count;
            double[] residual = new double[L];
            for (int ell = 0; ell < L; ell++)
            {
                int node = electrodes[ell].MeshId;
                residual[ell] = phi[node] - Uobs[ell];
            }

            // 2) Adjoint RHS: Sᵀ residual → nodal injection
            double[] adjSource = new double[N];
            for (int ell = 0; ell < L; ell++)
            {
                int node = electrodes[ell].MeshId;
                // add residual back at node
                adjSource[node] = residual[ell];
            }

            // 3) Solve adjoint state: L ψ = Sᵀ residual
            var psi = _lapSolver.SolveLaplacian(alpha.Zip(wbar, (a, wb) => a * wb).ToArray(), adjSource);

            // 4) Compute ∂J/∂w̄ and ∂J/∂α per edge
            var gradWbar = new double[E];
            var gradAlpha = new double[E];

            for (int e = 0; e < E; e++)
            {
                var (i, j) = _lapSolver._graph.Edges[e];

                // w_ij = α_e * w̄_e
                double wij = alpha[e] * wbar[e];

                // kernel: -(ψ_i-ψ_j)(φ_i-φ_j)
                double kernel = -(psi[i] - psi[j]) * (phi[i] - phi[j]);

                // ∂J/∂w_ij = kernel + 2λ_w w_ij
                double dJdw = kernel + 2.0 * _lambdaW * wij;

                // chain rule: ∂J/∂w̄ = α * ∂J/∂w
                gradWbar[e] = alpha[e] * dJdw;

                // ∂J/∂α = w̄ * ∂J/∂w + λ_α [logα - log(1-α)]
                double reg = Math.Log(alpha[e] + _epsilon) - Math.Log(1.0 - alpha[e] + _epsilon);
                gradAlpha[e] = wbar[e] * dJdw + _lambdaAlpha * reg;
            }

            return (gradWbar, gradAlpha);
        }
    }
}
