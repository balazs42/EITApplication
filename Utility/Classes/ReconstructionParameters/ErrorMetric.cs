using Google.OrTools.LinearSolver;
using Utility.Classes.Meshing;

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
                // If either value is NaN, this point doesn't contribute to the error.
                if (double.IsNaN(measured[i]) || double.IsNaN(simulated[i])) continue;
                
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

                // If a value is NaN, the residual (the source for the adjoint) should be zero.
                if (double.IsNaN(measured[i]) || double.IsNaN(simulated[i]))
                {
                    residual[i] = 0.0;
                }
                // This is the (Sφ - d_observed) term. The negative sign and S*
                // are handled by the adjoint solver itself.
                else
                {
                    residual[i] = simulated[i] - measured[i];
                }
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
            if (mesh is not LBMMesh lbmMesh)
                throw new ArgumentException("Wasserstein2ErrorMetric currently requires an LBMMesh to get element coordinates.");

            // 1. Get the list of physical electrodes and their locations from the mesh.
            var electrodes = lbmMesh.GetElectrodes().OrderBy(e => e.Id).ToList();
            if (electrodes.Count != measured.Length)
                throw new ArgumentException("Number of electrodes in mesh must match data length.");

            // 2. Normalize the data, filtering out any NaN values from driving electrodes.
            // This gives us the valid "masses" (mu and nu) and their corresponding locations.
            var (mu, muLocations) = Normalize(simulated, electrodes, lbmMesh);
            var (nu, nuLocations) = Normalize(measured, electrodes, lbmMesh);

            // It's possible for the sets of measuring electrodes to be different, but for EIT they are usually the same.
            // We'll proceed assuming we are comparing the distribution of `mu` to `nu`.
            int n_mu = mu.Length;
            int n_nu = nu.Length;

            // 3. Build the cost matrix C_ij = ||x_i - y_j||^2 between the valid locations.
            var costMatrix = new double[n_mu, n_nu];
            for (int i = 0; i < n_mu; i++)
            {
                for (int j = 0; j < n_nu; j++)
                {
                    double dx = muLocations[i].x - nuLocations[j].x;
                    double dy = muLocations[i].y - nuLocations[j].y;
                    costMatrix[i, j] = dx * dx + dy * dy;
                }
            }

            // 4. Formulate and solve the dual LP problem using Google OR-Tools.
            Solver solver = Solver.CreateSolver("GLOP");

            Variable[] phi_vars = new Variable[n_mu];
            Variable[] psi_vars = new Variable[n_nu];
            for (int i = 0; i < n_mu; ++i) phi_vars[i] = solver.MakeNumVar(double.NegativeInfinity, double.PositiveInfinity, $"phi_{i}");
            for (int i = 0; i < n_nu; ++i) psi_vars[i] = solver.MakeNumVar(double.NegativeInfinity, double.PositiveInfinity, $"psi_{i}");

            for (int i = 0; i < n_mu; i++)
            {
                for (int j = 0; j < n_nu; j++)
                {
                    Constraint constraint = solver.MakeConstraint(double.NegativeInfinity, costMatrix[i, j]);
                    constraint.SetCoefficient(phi_vars[i], 1);
                    constraint.SetCoefficient(psi_vars[j], 1);
                }
            }

            Objective objective = solver.Objective();
            for (int i = 0; i < n_mu; ++i) objective.SetCoefficient(phi_vars[i], mu[i]);
            for (int i = 0; i < n_nu; ++i) objective.SetCoefficient(psi_vars[i], nu[i]);
            objective.SetMaximization();

            if (solver.Solve() != Solver.ResultStatus.OPTIMAL)
                throw new Exception("Could not solve the Optimal Transport LP problem.");

            // 5. Extract results and map them back to the full 16-electrode vector.
            double optimalValue = solver.Objective().Value();
            var phi_result_dense = phi_vars.Select(v => v.SolutionValue()).ToArray();

            var phi_full = new double[electrodes.Count]; // Initialize to zeros
            int mu_idx = 0;
            for (int i = 0; i < electrodes.Count; i++)
            {
                // If the simulated potential at this electrode was a valid number,
                // it was included in the 'mu' distribution. Place its calculated
                // Kantorovich potential back into the full-size array.
                if (!double.IsNaN(simulated[i]))
                {
                    phi_full[i] = phi_result_dense[mu_idx];
                    mu_idx++;
                }
                // Otherwise, it remains zero.
            }

            return new OptimalTransportResult(measured, simulated, optimalValue, phi_full);
        }

        /// <summary>
        /// Normalizes a vector, filtering out NaN values, to create a probability distribution.
        /// Returns the valid "masses" and their corresponding (x, y) coordinates.
        /// </summary>
        private (double[] dist, List<(int x, int y)> locs) Normalize(double[] vector, List<Electrode> allElectrodes, LBMMesh mesh)
        {
            var validData = new List<double>();
            var validLocations = new List<(int x, int y)>();

            for (int i = 0; i < vector.Length; i++)
            {
                if (!double.IsNaN(vector[i]))
                {
                    validData.Add(vector[i]);
                    // Find the location of this valid data point using the electrode's MeshId
                    validLocations.Add(mesh.ToLattice(allElectrodes[i].MeshId));
                }
            }

            if (validData.Count == 0) return (Array.Empty<double>(), validLocations);

            var V = validData.ToArray();
            double min = V.Min();
            if (min < 0)
            {
                for (int i = 0; i < V.Length; i++) V[i] -= min;
            }
            double sum = V.Sum();
            if (sum < 1e-9) return (new double[V.Length], validLocations);

            for (int i = 0; i < V.Length; i++) V[i] /= sum;

            return (V, validLocations);
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