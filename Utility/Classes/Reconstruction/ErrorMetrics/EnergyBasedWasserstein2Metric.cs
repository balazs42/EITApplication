using System;
using System.Collections.Generic;
using System.Linq;
using Google.OrTools.LinearSolver;
using MathNet.Numerics.LinearAlgebra;
using MathNet.Numerics.LinearAlgebra.Double;
using MathNet.Numerics.LinearAlgebra.Factorization;
using Utility.Classes.Discretizer;
using Utility.Classes.Discretizer.FiniteElementMesh;
using Utility.Classes.ReconstructionParameters;

namespace Utility.Classes.Reconstruction.ErrorMetrics
{
    /// <summary>
    /// Implements a Wasserstein-2 misfit whose ground cost is the Dirichlet
    /// energy induced by the current conductivity distribution.  The optimal
    /// transport problem is solved with the LP workflow used by the existing
    /// Wasserstein metrics; only the ground cost differs.
    /// 
    /// MATHEMATICAL BACKGROUND:
    /// - Standard Wasserstein-2 uses squared Euclidean distance as ground cost
    /// - This implementation uses Dirichlet energy as the ground cost between electrode pairs
    /// - Dirichlet energy represents the energy required to transport charge between electrodes
    ///   through the conductive medium with current conductivity distribution
    /// - This makes the metric sensitive to the internal conductivity structure, not just boundary geometry
    /// </summary>
    public sealed class EnergyBasedWasserstein2Metric : IErrorMetric
    {
        /// <summary>
        /// Small numerical tolerance to avoid division by zero and handle degenerate cases
        /// </summary>
        private const double Tiny = 1e-12;

        private static double Sanitize(double value) => double.IsFinite(value) ? value : 0.0;

        private static void SanitizeInPlace(double[] values)
        {
            for (int i = 0; i < values.Length; i++)
                values[i] = Sanitize(values[i]);
        }
        
        /// <summary>
        /// Cache for the last computation to avoid redundant calculations when 
        /// evaluating both the objective and adjoint source with the same data
        /// </summary>
        private CachedResult? _last;

        /// <inheritdoc />
        /// <summary>
        /// MAIN EVALUATION METHOD:
        /// Computes the Wasserstein-2 distance between measured and simulated electrode data
        /// using Dirichlet energy as the ground cost metric.
        /// 
        /// ALGORITHM OVERVIEW:
        /// 1. Extract and normalize histograms from electrode data
        /// 2. Compute Dirichlet energy-based cost matrix between all electrode pairs
        /// 3. Solve optimal transport linear program
        /// 4. Return the optimal transport cost as the misfit value
        /// </summary>
        public double Evaluate(IDiscretization discretization, double[] measured, double[] simulated)
        {
            var result = Solve(discretization, measured, simulated);
            _last = result;
            return result.Cost;
        }

        /// <inheritdoc />
        /// <summary>
        /// ADJOINT SOURCE COMPUTATION:
        /// Computes the adjoint source term needed for gradient-based optimization.
        /// For Wasserstein metrics, this is derived from the optimal transport plan.
        /// 
        /// MATHEMATICAL INTERPRETATION:
        /// - Returns the Kantorovich potential (dual variable) from the optimal transport solution
        /// - This represents how much "influence" each electrode has in the optimal transport
        /// - Used as source term in adjoint PDE for computing parameter gradients
        /// </summary>
        public double[] EvaluateAdjointSource(IDiscretization discretization, double[] measured, double[] simulated)
        {
            // Use cached result if available to avoid redundant computation
            if (_last != null && _last.Matches(measured, simulated))
                return _last.Adjoint;

            var result = Solve(discretization, measured, simulated);
            _last = result;
            return result.Adjoint;
        }

        /// <summary>
        /// CORE SOLUTION METHOD:
        /// Implements the complete Wasserstein-2 computation pipeline with Dirichlet energy costs.
        /// 
        /// DETAILED ALGORITHM:
        /// 1. Input validation and preprocessing
        /// 2. Extract valid (non-NaN) electrode measurements  
        /// 3. Normalize data to probability distributions
        /// 4. Compute Dirichlet energy cost matrix via FEM analysis
        /// 5. Solve optimal transport linear program
        /// 6. Extract dual variables (Kantorovich potentials) for adjoint computation
        /// 7. Package results for caching and return
        /// </summary>
        private CachedResult Solve(IDiscretization discretization, double[] measured, double[] simulated)
        {
            // === INPUT VALIDATION ===
            if (discretization is null) throw new ArgumentNullException(nameof(discretization));
            if (measured is null) throw new ArgumentNullException(nameof(measured));
            if (simulated is null) throw new ArgumentNullException(nameof(simulated));

            if (measured.Length != simulated.Length)
                throw new ArgumentException("Measured and simulated arrays must have the same length.");

            // This implementation specifically requires FEM mesh for Dirichlet energy computation
            if (discretization.GetDiscretization() is not FEMMesh fem)
                throw new NotSupportedException("EnergyBasedWasserstein2Metric currently requires a FEM mesh discretization.");

            var electrodes = fem.GetElectrodes();
            if (electrodes.Count != measured.Length)
                throw new ArgumentException("Data length must match the number of electrodes in the discretization.");

            // === DATA PREPROCESSING ===
            // Extract indices of valid (non-NaN) measurements
            // Invalid measurements are excluded from the optimal transport problem
            var include = new List<int>(measured.Length);
            for (int i = 0; i < measured.Length; i++)
            {
                if (double.IsNaN(measured[i]))
                    continue;
                include.Add(i);
            }

            // Handle degenerate case: no valid measurements
            if (include.Count == 0)
                return CachedResult.Zero(measured, simulated);

            // === HISTOGRAM EXTRACTION AND NORMALIZATION ===
            // Extract electrode values for valid indices only
            var muRaw = ExtractHistogram(simulated, include);  // Source distribution (simulated)
            var nuRaw = ExtractHistogram(measured, include);   // Target distribution (measured)

            // Normalize to probability distributions (non-negative, sum to 1)
            var mu = NormalizeHistogram(muRaw);
            var nu = NormalizeHistogram(nuRaw);

            // Handle degenerate distributions (all zeros or invalid)
            if (mu.IsDegenerate || nu.IsDegenerate)
                return CachedResult.Zero(measured, simulated);

            // === DIRICHLET ENERGY COST MATRIX COMPUTATION ===
            // This is the key innovation: use Dirichlet energy instead of Euclidean distance
            var energy = new ElectrodeEnergyOperator(fem);
            var cost = energy.BuildCostMatrix(include);

            // === OPTIMAL TRANSPORT SOLUTION ===
            // Solve the linear programming formulation of optimal transport:
            // min_{P} sum_{i,j} cost[i,j] * P[i,j]
            // subject to: sum_j P[i,j] = mu[i], sum_i P[i,j] = nu[j], P[i,j] >= 0
            var transport = SolveOptimalTransport(cost, mu.Values, nu.Values);
            double loss = Sanitize(transport.Objective);

            // === ADJOINT SOURCE COMPUTATION ===
            // Extract Kantorovich potential (dual variable alpha) from transport solution
            var sourcePotential = (double[])transport.Alpha.Clone();
            SanitizeInPlace(sourcePotential);
            
            // Compute weighted mean of potential (needed for gauge fixing)
            double weightedMean = 0.0;
            for (int i = 0; i < sourcePotential.Length; i++)
                weightedMean += sourcePotential[i] * mu.Values[i];

            // Apply gauge fixing and mass normalization to get adjoint source
            double invMass = 1.0 / mu.TotalMass;
            var sourceGradient = new double[sourcePotential.Length];
            for (int i = 0; i < sourceGradient.Length; i++)
                sourceGradient[i] = Sanitize((sourcePotential[i] - weightedMean) * invMass);

            SanitizeInPlace(sourceGradient);

            // Map back to full electrode array (including NaN entries)
            var adjointFull = new double[measured.Length];
            for (int i = 0; i < include.Count; i++)
                adjointFull[include[i]] = sourceGradient[i];

            return new CachedResult(measured, simulated, loss, adjointFull, include.ToArray(), sourcePotential, sourceGradient, transport.Plan);
        }

        /// <summary>
        /// HISTOGRAM NORMALIZATION:
        /// Converts raw electrode measurements to a valid probability distribution.
        /// 
        /// PROCESS:
        /// 1. Clamp negative values to zero (physical constraint)
        /// 2. Replace invalid (NaN/infinite) values with zero
        /// 3. Normalize to sum to 1 (probability distribution requirement)
        /// 4. Handle degenerate case where all values are zero/invalid
        /// </summary>
        private static Histogram NormalizeHistogram(double[] raw)
        {
            double[] values = (double[])raw.Clone();
            double sum = 0.0;
            
            // Clean and validate values
            for (int i = 0; i < values.Length; i++)
            {
                double v = values[i];
                if (!double.IsFinite(v))  // Handle NaN and infinity
                    v = 0.0;
                if (v < 0.0)             // Physical constraint: non-negative
                    v = 0.0;
                values[i] = v;
                sum += v;
            }

            // Check for degenerate distribution
            if (sum <= Tiny)
                return Histogram.Degenerate(values);

            // Normalize to probability distribution
            for (int i = 0; i < values.Length; i++)
                values[i] /= sum;

            return new Histogram(values, false, sum);
        }

        /// <summary>
        /// ELECTRODE DATA EXTRACTION:
        /// Extracts electrode measurements for specified indices, handling invalid values.
        /// </summary>
        private static double[] ExtractHistogram(double[] data, IReadOnlyList<int> include)
        {
            var result = new double[include.Count];
            for (int i = 0; i < include.Count; i++)
            {
                double value = data[include[i]];
                if (!double.IsFinite(value))
                    value = 0.0;
                result[i] = value;
            }
            return result;
        }

        /// <summary>
        /// OPTIMAL TRANSPORT LINEAR PROGRAM SOLVER:
        /// Solves the discrete optimal transport problem using Google OR-Tools.
        /// 
        /// MATHEMATICAL FORMULATION:
        /// minimize: sum_{i,j} cost[i,j] * P[i,j]
        /// subject to:
        ///   - sum_j P[i,j] = source[i]  (source marginal constraints)
        ///   - sum_i P[i,j] = target[j]  (target marginal constraints)  
        ///   - P[i,j] >= 0               (non-negativity)
        /// 
        /// RETURNS:
        /// - Optimal transport plan P[i,j]
        /// - Dual variables (Kantorovich potentials) alpha[i], beta[j]
        /// - Optimal objective value
        /// </summary>
        private static OptimalTransportSolution SolveOptimalTransport(double[,] cost, double[] source, double[] target)
        {
            int m = source.Length;  // Number of source points (simulated electrodes)
            int n = target.Length;  // Number of target points (measured electrodes)

            // Initialize OR-Tools linear programming solver
            var solver = Solver.CreateSolver("GLOP")
                         ?? throw new InvalidOperationException("OR-Tools LP solver 'GLOP' not available.");

            // === DECISION VARIABLES ===
            // P[i,j] = amount of mass transported from source i to target j
            var plan = new Variable[m, n];
            
            // === CONSTRAINTS ===
            var row = new Constraint[m];  // Source marginal constraints
            var col = new Constraint[n];  // Target marginal constraints

            // Source marginal constraints: sum_j P[i,j] = source[i]
            for (int i = 0; i < m; i++)
                row[i] = solver.MakeConstraint(source[i], source[i], $"row[{i}]");
            
            // Target marginal constraints: sum_i P[i,j] = target[j]
            for (int j = 0; j < n; j++)
                col[j] = solver.MakeConstraint(target[j], target[j], $"col[{j}]");

            // === OBJECTIVE FUNCTION ===
            var objective = solver.Objective();

            // Create variables and set up constraint coefficients
            for (int i = 0; i < m; i++)
            {
                for (int j = 0; j < n; j++)
                {
                    // Transport variable: P[i,j] >= 0
                    plan[i, j] = solver.MakeNumVar(0.0, double.PositiveInfinity, $"P[{i},{j}]");
                    
                    // Add to source constraint: P[i,j] contributes to row i
                    row[i].SetCoefficient(plan[i, j], 1.0);
                    
                    // Add to target constraint: P[i,j] contributes to column j
                    col[j].SetCoefficient(plan[i, j], 1.0);
                    
                    // Add to objective: cost[i,j] * P[i,j]
                    objective.SetCoefficient(plan[i, j], cost[i, j]);
                }
            }

            // Minimize total transport cost
            objective.SetMinimization();
            
            // === SOLVE ===
            var status = solver.Solve();
            if (status != Solver.ResultStatus.OPTIMAL)
                throw new InvalidOperationException($"Optimal transport LP failed with status {status}.");

            // === EXTRACT SOLUTION ===
            // Extract optimal transport plan
            var planMatrix = new double[m, n];
            for (int i = 0; i < m; i++)
                for (int j = 0; j < n; j++)
                    planMatrix[i, j] = plan[i, j].SolutionValue();

            // Compute objective value
            double objectiveValue = WeightedSum(cost, planMatrix);

            // Extract dual variables (Kantorovich potentials)
            var alpha = new double[m];  // Source potentials
            var beta = new double[n];   // Target potentials
            for (int i = 0; i < m; i++)
                alpha[i] = row[i].DualValue();
            for (int j = 0; j < n; j++)
                beta[j] = col[j].DualValue();

            return new OptimalTransportSolution(planMatrix, alpha, beta, objectiveValue);
        }

        /// <summary>
        /// UTILITY: Computes weighted sum of two matrices (element-wise product then sum)
        /// Used to compute the optimal transport objective value.
        /// </summary>
        private static double WeightedSum(double[,] matrix, double[,] plan)
        {
            int m = matrix.GetLength(0);
            int n = matrix.GetLength(1);
            double sum = 0.0;
            for (int i = 0; i < m; i++)
                for (int j = 0; j < n; j++)
                    sum += matrix[i, j] * plan[i, j];
            return sum;
        }

        /// <summary>
        /// DATA STRUCTURE: Represents a probability histogram with metadata
        /// Used to track normalization state and handle degenerate cases.
        /// </summary>
        private readonly struct Histogram
        {
            public Histogram(double[] values, bool degenerate, double totalMass)
            {
                Values = values;
                IsDegenerate = degenerate;
                TotalMass = totalMass;
            }

            /// <summary>Normalized probability values (sum to 1)</summary>
            public double[] Values { get; }
            
            /// <summary>True if distribution is degenerate (all zeros)</summary>
            public bool IsDegenerate { get; }
            
            /// <summary>Original total mass before normalization</summary>
            public double TotalMass { get; }

            /// <summary>Factory method for degenerate (all-zero) histograms</summary>
            public static Histogram Degenerate(double[] values) => new(values, true, 0.0);
        }

        /// <summary>
        /// DATA STRUCTURE: Caches computation results to avoid redundant calculations
        /// When evaluating both objective and adjoint source, the expensive optimal transport
        /// solve only needs to be done once.
        /// </summary>
        private sealed class CachedResult
        {
            public CachedResult(double[] measured, double[] simulated, double cost, double[] adjoint,
                                int[] included, double[] sourcePotential, double[] sourceGradient, double[,] plan)
            {
                Measured = measured;
                Simulated = simulated;
                Cost = cost;
                Adjoint = adjoint;
                Included = included;
                SourcePotential = sourcePotential;
                SourceGradient = sourceGradient;
                Plan = plan;
            }

            /// <summary>Original measured data (for cache validation)</summary>
            public double[] Measured { get; }
            
            /// <summary>Original simulated data (for cache validation)</summary>
            public double[] Simulated { get; }
            
            /// <summary>Computed Wasserstein-2 cost</summary>
            public double Cost { get; }
            
            /// <summary>Adjoint source term for gradient computation</summary>
            public double[] Adjoint { get; }
            
            /// <summary>Indices of valid (non-NaN) electrodes</summary>
            public int[] Included { get; }
            
            /// <summary>Raw Kantorovich potential from optimal transport</summary>
            public double[] SourcePotential { get; }
            
            /// <summary>Processed gradient for adjoint source</summary>
            public double[] SourceGradient { get; }
            
            /// <summary>Optimal transport plan matrix</summary>
            public double[,] Plan { get; }

            /// <summary>
            /// Cache validation: checks if current data matches cached computation
            /// Uses reference equality for efficiency (assumes data arrays don't change)
            /// </summary>
            public bool Matches(double[] measured, double[] simulated)
                => ReferenceEquals(Measured, measured) && ReferenceEquals(Simulated, simulated);

            /// <summary>Factory method for zero/degenerate results</summary>
            public static CachedResult Zero(double[] measured, double[] simulated)
            {
                var zeros = new double[measured.Length];
                return new CachedResult(measured, simulated, 0.0, zeros, Array.Empty<int>(), Array.Empty<double>(), Array.Empty<double>(), new double[0, 0]);
            }
        }

        /// <summary>
        /// DATA STRUCTURE: Packages optimal transport solution components
        /// Contains both primal (transport plan) and dual (Kantorovich potentials) solutions.
        /// </summary>
        private sealed record OptimalTransportSolution(double[,] Plan, double[] Alpha, double[] Beta, double Objective);

        /// <summary>
        /// DIRICHLET ENERGY OPERATOR:
        /// Computes the Dirichlet energy between electrode pairs using finite element analysis.
        /// This is the core innovation that distinguishes this metric from standard Wasserstein-2.
        /// 
        /// MATHEMATICAL BACKGROUND:
        /// - Dirichlet energy measures the "electrical energy" required to transport unit charge
        ///   between two electrodes through the conductive medium
        /// - Computed by solving electrostatic boundary value problems on the FEM mesh
        /// - Energy depends on the current conductivity distribution, making the metric
        ///   sensitive to internal structure, not just boundary geometry
        /// 
        /// ALGORITHM:
        /// 1. Assemble global stiffness matrix from FEM elements
        /// 2. Add contact impedance contributions from electrodes
        /// 3. Form Schur complement to eliminate internal nodes
        /// 4. For each electrode pair, solve for voltage distribution and compute energy
        /// </summary>
        private sealed class ElectrodeEnergyOperator
        {
            /// <summary>Minimum contact impedance to avoid numerical issues</summary>
            private const double ContactImpedanceFloor = 1e-12;

            /// <summary>Schur complement matrix (electrode-to-electrode interactions)</summary>
            private readonly DenseMatrix _schur;
            
            /// <summary>Number of electrodes in the mesh</summary>
            private readonly int _electrodeCount;
            
            /// <summary>Index of gauge electrode (reference potential)</summary>
            private readonly int _gaugeIndex;
            
            /// <summary>Cholesky factorization of gauge-reduced system</summary>
            private readonly Cholesky<double>? _reducedFactorization;

            /// <summary>
            /// CONSTRUCTOR: Precomputes FEM matrices for efficient energy calculations
            /// 
            /// FINITE ELEMENT ASSEMBLY PROCESS:
            /// 1. Assemble global stiffness matrix from element contributions
            /// 2. Add electrode contact impedance terms  
            /// 3. Form electrode coupling matrix
            /// 4. Compute Schur complement to eliminate internal degrees of freedom
            /// 5. Factor the gauge-reduced system for efficient energy computations
            /// </summary>
            public ElectrodeEnergyOperator(FEMMesh mesh)
            {
                var electrodes = mesh.GetElectrodes().Cast<FEMElectrode>().ToList();
                _electrodeCount = electrodes.Count;
                _gaugeIndex = Math.Max(0, _electrodeCount - 1);  // Use last electrode as ground reference

                // Handle trivial case: no electrodes
                if (_electrodeCount == 0)
                {
                    _schur = DenseMatrix.Create(0, 0, 0.0);
                    _reducedFactorization = null;
                    return;
                }

                int nodeCount = mesh.Vertices.Count;
                double[,] stiffnessArray = new double[nodeCount, nodeCount];

                // === ELEMENT STIFFNESS ASSEMBLY ===
                // Sum contributions from all finite elements
                // Each element contributes: sigma * area * grad_phi_i  grad_phi_j
                foreach (var element in mesh.ElementsTyped.Cast<FEMElement>())
                {
                    double sigma = element.Conductivity;  // Local conductivity
                    double area = element.Area;           // Element area
                    
                    // Add element stiffness to global matrix
                    for (int a = 0; a < element.Vertices.Count; a++)
                    {
                        int i = element.Vertices[a].GlobalId;
                        for (int b = 0; b < element.Vertices.Count; b++)
                        {
                            int j = element.Vertices[b].GlobalId;
                            double dot = element.DotProducts[a][b];  // Gradient dot product
                            stiffnessArray[i, j] += sigma * area * dot;
                        }
                    }
                }

                // === ELECTRODE COUPLING ASSEMBLY ===
                double[,] couplingArray = new double[_electrodeCount, nodeCount];  // Electrode-to-node coupling
                double[,] diagArray = new double[_electrodeCount, _electrodeCount]; // Electrode self-terms

                for (int ell = 0; ell < electrodes.Count; ell++)
                {
                    var electrode = electrodes[ell];
                    double z = electrode.ZContact;  // Contact impedance
                    double invZ = 1.0 / Math.Max(z, ContactImpedanceFloor);  // Conductance
                    double length = electrode.Length;  // Electrode length
                    
                    // Determine how electrode couples to mesh nodes
                    int nodeMultiplicity = Math.Max(1, electrode.FEMVertexIds?.Count ?? 0);
                    double h = length / nodeMultiplicity;  // Length per node

                    // Add electrode contributions to stiffness and coupling matrices
                    if (electrode.FEMVertexIds != null && electrode.FEMVertexIds.Count > 0)
                    {
                        // Multi-node electrode: distribute over multiple vertices
                        foreach (int vid in electrode.FEMVertexIds)
                        {
                            stiffnessArray[vid, vid] += invZ * h;      // Node self-term
                            couplingArray[ell, vid] += invZ * h;      // Electrode-node coupling
                        }
                    }
                    else
                    {
                        // Point electrode: couple to single vertex
                        int vid = electrode.MeshId;
                        stiffnessArray[vid, vid] += invZ * h;
                        couplingArray[ell, vid] += invZ * h;
                    }

                    // Electrode self-term
                    diagArray[ell, ell] = length * invZ;
                }

                // === SCHUR COMPLEMENT COMPUTATION ===
                // Eliminate internal degrees of freedom to get electrode-only system
                // Schur = D - C * K^(-1) * C^T
                // where K = stiffness, C = coupling, D = diagonal
                
                var stiffness = DenseMatrix.OfArray(stiffnessArray);
                var coupling = DenseMatrix.OfArray(couplingArray);
                var diag = DenseMatrix.OfArray(diagArray);

                // Solve K * X = C^T  =>  X = K^(-1) * C^T
                var factor = stiffness.Cholesky();
                var kInvBt = factor.Solve(coupling.Transpose());
                
                // Compute Schur complement: S = D - C * K^(-1) * C^T
                var schur = diag - coupling * kInvBt;
                SymmetrizeInPlace((DenseMatrix)schur);  // Enforce symmetry for numerical stability
                _schur = (DenseMatrix)schur;

                // === GAUGE REDUCTION AND FACTORIZATION ===
                // Remove gauge degree of freedom (fix one electrode potential to zero)
                // This makes the system invertible for energy computations
                
                if (_electrodeCount <= 1)
                {
                    _reducedFactorization = null;
                    return;
                }

                var reduced = RemoveGauge(_schur, _gaugeIndex);
                
                // Add small regularization for numerical stability
                for (int i = 0; i < reduced.RowCount; i++)
                    reduced[i, i] += 1e-12;
                    
                _reducedFactorization = reduced.Cholesky();
            }

            /// <summary>
            /// COST MATRIX COMPUTATION:
            /// Builds the full cost matrix for optimal transport using Dirichlet energies.
            /// 
            /// For each pair of electrodes (i,j), computes the Dirichlet energy required
            /// to transport unit charge from electrode i to electrode j through the 
            /// conductive medium.
            /// </summary>
            public double[,] BuildCostMatrix(IReadOnlyList<int> indices)
            {
                int m = indices.Count;
                var result = new double[m, m];
                if (m == 0)
                    return result;

                // Compute ground cost for each electrode pair
                for (int i = 0; i < m; i++)
                {
                    result[i, i] = 0.0;  // No cost to transport from electrode to itself
                    for (int j = i + 1; j < m; j++)
                    {
                        double cost = GroundCost(indices[i], indices[j]);
                        result[i, j] = cost;
                        result[j, i] = cost;  // Symmetric cost matrix
                    }
                }
                return result;
            }

            /// <summary>
            /// DIRICHLET ENERGY COMPUTATION:
            /// Computes the Dirichlet energy between two specific electrodes.
            /// 
            /// ALGORITHM:
            /// 1. Set up boundary value problem: inject +1 current at electrode a, -1 at electrode b
            /// 2. Solve for electrode potentials using Schur complement system
            /// 3. Compute energy as phi^T * S * phi, where phi is potential vector and S is Schur complement
            /// 
            /// PHYSICAL INTERPRETATION:
            /// - Energy represents work required to move unit charge between electrodes
            /// - Depends on conductivity distribution: higher conductivity => lower energy
            /// - Incorporates contact impedances and electrode geometry
            /// </summary>
            private double GroundCost(int a, int b)
            {
                // Trivial cases
                if (_electrodeCount == 0 || a == b)
                    return 0.0;

                if (_electrodeCount == 1)
                    return 0.0;

                // === BOUNDARY VALUE PROBLEM SETUP ===
                // Create right-hand side for current injection:
                // +1 current at electrode a, -1 current at electrode b
                // (gauge electrode excluded from system)
                
                var rhs = new double[_electrodeCount - 1];
                int idx = 0;
                for (int k = 0; k < _electrodeCount; k++)
                {
                    if (k == _gaugeIndex)
                        continue;  // Skip gauge electrode

                    double value = 0.0;
                    if (k == a) value += 1.0;   // Inject current at electrode a
                    if (k == b) value -= 1.0;   // Extract current at electrode b
                    rhs[idx++] = value;
                }

                // === SOLVE FOR ELECTRODE POTENTIALS ===
                // Solve reduced Schur complement system: S_reduced * phi = rhs
                var rhsVec = DenseVector.OfArray(rhs);
                var sol = _reducedFactorization!.Solve(rhsVec);
                
                // === COMPUTE DIRICHLET ENERGY ===
                // Energy = phi^T * S * phi = rhs^T * phi (due to S * phi = rhs)
                return rhsVec.DotProduct(sol);
            }

            /// <summary>
            /// UTILITY: Removes gauge degree of freedom from matrix
            /// Eliminates specified row and column to make system invertible.
            /// </summary>
            private static DenseMatrix RemoveGauge(Matrix<double> matrix, int gauge)
            {
                int n = matrix.RowCount;
                var reduced = new double[n - 1, n - 1];
                int row = 0;
                for (int i = 0; i < n; i++)
                {
                    if (i == gauge) continue;
                    int col = 0;
                    for (int j = 0; j < n; j++)
                    {
                        if (j == gauge) continue;
                        reduced[row, col++] = matrix[i, j];
                    }
                    row++;
                }
                return DenseMatrix.OfArray(reduced);
            }

            /// <summary>
            /// UTILITY: Enforces matrix symmetry for numerical stability
            /// Averages off-diagonal elements to ensure exact symmetry.
            /// </summary>
            private static void SymmetrizeInPlace(DenseMatrix matrix)
            {
                int n = matrix.RowCount;
                for (int i = 0; i < n; i++)
                    for (int j = i + 1; j < n; j++)
                    {
                        double avg = 0.5 * (matrix[i, j] + matrix[j, i]);
                        matrix[i, j] = avg;
                        matrix[j, i] = avg;
                    }
            }
        }
    }
}
