using Utility.Classes.Meshing;
using Utility.Classes.ReconstructionParameters;

namespace Utility.Classes.Solvers
{
    public class GraphBasedSolver
    {
        private readonly GraphAssembler _assembler;
        private readonly GraphBasedOperators _lapSolver;
        private readonly AdjointGradient _adjGrad;

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

        /// <summary>
        /// Performs one inversion iteration:
        ///   - forward: L(w) φ = I
        ///   - adjoint: L(w) ψ = Sᵀ(φ - U_obs)
        ///   - gradient update of w̄ and α
        /// </summary>
        public void Iteration(FEMMesh mesh, INumericSolver femSolver)
        {
            int N = _assembler.NodeCount;
            int E = _assembler.Edges.Count;

            // 1) Build current edge weights w = α·w̄
            var w = new double[E];
            for (int e = 0; e < E; e++)
                w[e] = _alpha[e] * _wbar[e];

            // 2) Build RHS currents I from electrodes in mesh
            var electrodes = mesh.Electrodes;
            var I = new double[N];
            foreach (var el in electrodes)
                I[el.MeshId] = el.Current;

            // 3) Forward solve for φ
            var phi = _lapSolver.SolveLaplacian(w, I);

            // 4) Collect observed voltages U_obs
            var Uobs = new double[electrodes.Count];
            for (int ℓ = 0; ℓ < electrodes.Count; ℓ++)
                Uobs[ℓ] = electrodes[ℓ].Potential;

            // 5) Compute adjoint gradients
            var (gradWbar, gradAlpha) = _adjGrad.Compute(_wbar, _alpha, phi, Uobs);

            // 6) Gradient descent updates
            for (int e = 0; e < E; e++)
            {
                // update w̄_e
                _wbar[e] -= _stepW * gradWbar[e];
                // update α_e and clamp into [ε,1-ε]
                _alpha[e] = Math.Clamp(_alpha[e] - _stepAlpha * gradAlpha[e],
                                        _epsilon, 1.0 - _epsilon);
            }

            // 7) (optionally) inject updated σ back into mesh if needed
            // e.g. mesh.SetConductivityDistribution(...)
        }
    }
}
