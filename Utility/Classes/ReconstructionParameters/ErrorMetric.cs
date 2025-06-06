using Google.OrTools.LinearSolver;

namespace Utility.Classes.ReconstructionParameters
{
    public enum ErrorMetric
    {
        L2 = 1,
        Wasserstein2 = 2
    }

    /// <summary>
    /// Defines a misfit functional that measures the discrepancy between
    /// measured and simulated data.
    /// </summary>
    public interface IErrorMetric
    {
        /// <summary>
        /// Evaluates the misfit functional, J_misfit.
        /// This corresponds to the first term in your total cost function.
        /// </summary>
        /// <param name="measured">Observed boundary potentials.</param>
        /// <param name="simulated">Simulated boundary potentials from the forward model.</param>
        /// <returns>A scalar value representing the misfit.</returns>
        double Evaluate(IMesh mesh, double[] measured, double[] simulated);

        /// <summary>
        /// Evaluates the source term for the adjoint PDE problem.
        /// For L2, this is the residual (simulated - measured).
        /// For W2, this is the Kantorovich potential, φ.
        /// </summary>
        /// <param name="measured">Observed boundary potentials.</param>
        /// <param name="simulated">Simulated boundary potentials from the forward model.</param>
        /// <returns>A vector to be used as the source on the right-hand-side of the adjoint PDE.</returns>
        double[] EvaluateAdjointSource(IMesh mesh, double[] measured, double[] simulated);
    }

    /// <summary>
    /// Implements the standard L2-norm squared misfit. J = 1/2 * ||d_sim - d_obs||^2.
    /// </summary>
    public sealed class L2ErrorMetric : IErrorMetric
    {
        public double Evaluate(IMesh mesh, double[] measured, double[] simulated)
        {
            if (measured.Length != simulated.Length)
                throw new ArgumentException("Measured and simulated vectors must have the same length.");

            double sumOfSquares = 0.0;
            for (int i = 0; i < measured.Length; i++)
            {
                double residual = simulated[i] - measured[i];
                sumOfSquares += residual * residual;
            }
            return 0.5 * sumOfSquares;
        }

        public double[] EvaluateAdjointSource(IMesh mesh, double[] measured, double[] simulated)
        {
            if (measured.Length != simulated.Length)
                throw new ArgumentException("Measured and simulated vectors must have the same length.");

            double[] residual = new double[measured.Length];
            for (int i = 0; i < measured.Length; i++)
            {
                // This is the (Sφ - d_observed) term. The negative sign and S*
                // are handled by the adjoint solver itself.
                residual[i] = simulated[i] - measured[i];
            }
            return residual;
        }
    }

    /// <summary>
    /// Implements the Wasserstein-2 metric by solving the dual optimal transport problem using Google OR-Tools.
    /// </summary>
    public sealed class Wasserstein2ErrorMetric : IErrorMetric
    {
        // Cache the result of the latest LP solve to avoid re-computing
        private OptimalTransportResult _lastResult;

        public double Evaluate(IMesh mesh, double[] measured, double[] simulated)
        {
            _lastResult = SolveOptimalTransport(mesh, measured, simulated);
            return 0.5 * _lastResult.OptimalValue;
        }

        public double[] EvaluateAdjointSource(IMesh mesh, double[] measured, double[] simulated)
        {
            if (_lastResult == null || !_lastResult.MatchesInputs(measured, simulated))
            {
                _lastResult = SolveOptimalTransport(mesh, measured, simulated);
            }
            return _lastResult.Phi;
        }

        private OptimalTransportResult SolveOptimalTransport(IMesh mesh, double[] measured, double[] simulated)
        {
            // 1. Get physical locations and normalize data
            var locations = mesh.GetElectrodeVertices().OrderBy(v => v.ElectrodeId).ToList();
            int n = locations.Count;
            if (n != measured.Length || n != simulated.Length)
                throw new ArgumentException("Number of electrodes in mesh must match data length.");

            var mu = Normalize(simulated);
            var nu = Normalize(measured);

            // 2. Build the cost matrix C_ij = ||x_i - x_j||^2
            var costMatrix = new double[n, n];
            for (int i = 0; i < n; i++)
            {
                for (int j = 0; j < n; j++)
                {
                    double dx = locations[i].X - locations[j].X;
                    double dy = locations[i].Y - locations[j].Y;
                    costMatrix[i, j] = dx * dx + dy * dy;
                }
            }

            // 4. Formulate the dual LP problem using Google OR-Tools
            Solver solver = Solver.CreateSolver("GLOP"); // GLOP is Google's high-performance LP solver

            // Create variables: φ_i and ψ_j
            Variable[] phi_vars = new Variable[n];
            Variable[] psi_vars = new Variable[n];
            for (int i = 0; i < n; ++i)
            {
                phi_vars[i] = solver.MakeNumVar(double.NegativeInfinity, double.PositiveInfinity, $"phi_{i}");
                psi_vars[i] = solver.MakeNumVar(double.NegativeInfinity, double.PositiveInfinity, $"psi_{i}");
            }

            // Define constraints: φ_i + ψ_j <= C_ij
            for (int i = 0; i < n; i++)
            {
                for (int j = 0; j < n; j++)
                {
                    Constraint constraint = solver.MakeConstraint(double.NegativeInfinity, costMatrix[i, j], $"c_{i}_{j}");
                    constraint.SetCoefficient(phi_vars[i], 1);
                    constraint.SetCoefficient(psi_vars[j], 1);
                }
            }

            // Define objective: Maximize Σ φ_i * μ_i + Σ ψ_j * ν_j
            Objective objective = solver.Objective();
            for (int i = 0; i < n; ++i)
            {
                objective.SetCoefficient(phi_vars[i], mu[i]);
                objective.SetCoefficient(psi_vars[i], nu[i]);

            }
            objective.SetMaximization();

            // 5. Solve the LP problem
            Solver.ResultStatus resultStatus = solver.Solve();
            if (resultStatus != Solver.ResultStatus.OPTIMAL)
            {
                throw new Exception("Could not solve the Optimal Transport LP problem. Solver status: " + resultStatus);
            }

            // 6. Extract the results
            double optimalValue = solver.Objective().Value();
            var phi = new double[n];
            for (int i = 0; i < n; ++i)
            {
                phi[i] = phi_vars[i].SolutionValue();
            }

            return new OptimalTransportResult(measured, simulated, optimalValue, phi);
        }

        private double[] Normalize(double[] vector)
        {
            var V = (double[])vector.Clone();
            double min = V.Min();
            if (min < 0)
            {
                for (int i = 0; i < V.Length; i++) V[i] -= min;
            }
            double sum = V.Sum();
            if (sum < 1e-9) return new double[V.Length];
            for (int i = 0; i < V.Length; i++) V[i] /= sum;
            return V;
        }

        private sealed class OptimalTransportResult
        {
            private readonly double[] _inputMeasured;
            private readonly double[] _inputSimulated;
            public double OptimalValue { get; }
            public double[] Phi { get; }

            public OptimalTransportResult(double[] measured, double[] simulated, double value, double[] phi)
            {
                _inputMeasured = measured;
                _inputSimulated = simulated;
                OptimalValue = value;
                Phi = phi;
            }
            public bool MatchesInputs(double[] measured, double[] simulated) =>
                 _inputMeasured.SequenceEqual(measured) && _inputSimulated.SequenceEqual(simulated);
        }
    }
}