using System;
using System.Collections.Generic;
using System.Linq;
using Google.OrTools.LinearSolver;
using MathNet.Numerics.LinearAlgebra;
using MathNet.Numerics.LinearAlgebra.Double;
using MathNet.Numerics.LinearAlgebra.Factorization;
using Utility.Classes;
using Utility.Classes.Discretizer;
using Utility.Classes.Discretizer.FiniteElementMesh;
using Utility.Classes.Discretizer.GraphMesh;

using Utility.Classes.ReconstructionParameters;

namespace Utility.Classes.Reconstruction.ErrorMetrics
{
    /// <summary>

    /// Implements the conductivity-aware Wasserstein-2 error metric described in the
    /// user specification.  The class follows the existing <see cref="IErrorMetric"/>
    /// contract, while internally computing the optimal transport objective,
    /// Kantorovich potentials, and the adjoint conductivity gradient.
    /// </summary>
    public sealed class ConductivityAwareW2Metric : IErrorMetric
    {
        /// <summary>
        /// Configuration container exposed to callers.  Defaults follow the
        /// guidelines of the user specification.
        /// </summary>
        public sealed class Config
        {
            /// <summary>Exponent β in c_e(σ) = ℓ_e σ_e^{-β}.</summary>
            public double Beta { get; init; } = 1.0;

            /// <summary>Soft-min temperature τ in the entropy regularised Bellman operator.</summary>
            public double Tau { get; init; } = 0.05;

            /// <summary>
            /// Blend between geometric and conductivity-aware costs.  α = 0 uses
            /// purely geometric distances, α = 1 uses the conductivity-aware
            /// ground cost.
            /// </summary>
            public double Alpha { get; init; } = 0.0;

            /// <summary>Softplus temperature κ used in the smooth normalisation map.</summary>
            public double Kappa { get; init; } = 8.0;

            /// <summary>Mass floor ε added before normalisation.</summary>
            public double Epsilon { get; init; } = 1e-6;

            /// <summary>Maximum iterations in the soft Bellman solver.</summary>
            public int MaxBellmanIterations { get; init; } = 500;

            /// <summary>Absolute convergence tolerance for the Bellman iteration.</summary>
            public double BellmanTolerance { get; init; } = 1e-6;

            /// <summary>Relaxation parameter used when updating Bellman iterates.</summary>
            public double BellmanDamping { get; init; } = 0.5;

            /// <summary>If true the metric evaluates α = 1 regardless of <see cref="Alpha"/>.</summary>
            public bool UsePhysicsAwareOnly { get; init; } = false;

            /// <summary>When enabled the metric exposes individual gradient components.</summary>
            public bool ReturnComponents { get; init; } = false;

            /// <summary>Optional lower bound applied to edge conductivities.</summary>
            public double SigmaFloor { get; init; } = 1e-8;

            /// <summary>Optional upper bound applied to edge conductivities.</summary>
            public double SigmaCeiling { get; init; } = 1e6;
        }

        /// <summary>Cache structure produced by <see cref="Normalize"/>.</summary>
        internal readonly struct NormalizationCache
        {
            public NormalizationCache(double[] raw, double[] shifted, double[] softplus, double[] sigmoid,
                                      double[] normalized, int minIndex, double sum)
            {
                Raw = raw;
                Shifted = shifted;
                Softplus = softplus;
                Sigmoid = sigmoid;
                Normalized = normalized;
                MinIndex = minIndex;
                Sum = sum;
            }

            public double[] Raw { get; }
            public double[] Shifted { get; }
            public double[] Softplus { get; }
            public double[] Sigmoid { get; }
            public double[] Normalized { get; }
            public int MinIndex { get; }
            public double Sum { get; }
        }

        internal sealed record EdgeInfo(int Index, int U, int V, double Length, int ElementU, int ElementV, double Sigma, double Cost);

        private sealed record DirectedEdge(int EdgeIndex, int From, int To, double Cost);

        private sealed record Transition(int EdgeIndex, int To, double Probability);

        internal sealed class SoftGeodesicResult
        {
            public SoftGeodesicResult(double[,] distances,
                                      Dictionary<(int source, int target), double[]> occupancies,
                                      IReadOnlyList<EdgeInfo> edgeInfos)
            {
                Distances = distances;
                Occupancies = occupancies;
                EdgeInfos = edgeInfos;
            }

            public double[,] Distances { get; }
            public Dictionary<(int source, int target), double[]> Occupancies { get; }
            public IReadOnlyList<EdgeInfo> EdgeInfos { get; }
        }

        private sealed class EvaluationCache
        {
            public required double[] Mu;
            public required double[] Nu;
            public required double[,] GammaPhysics;
            public double[,]? GammaGeo;
            public required double[] AlphaPhysics;
            public double[]? AlphaGeo;
            public required NormalizationCache SimNormalization;
            public required SoftGeodesicResult Geodesics;
            public required double[] GMu;
            public required double[] AdjointElectrodeSource;
            public Dictionary<int, double>? GradientAdjointTerm;
            public required Dictionary<int, double> GradientCostTerm;
            public required double[] Measured;
            public required double[] Simulated;

            public ConductivityDistribution? Gradient;
        }

        private readonly Config _config;

        private EvaluationCache? _last;

        /// <summary>
        /// Creates a new conductivity-aware W₂ metric.
        /// </summary>
        /// <param name="config">Optional configuration overrides.</param>
        public ConductivityAwareW2Metric(Config? config = null)
        {
            _config = config ?? new Config();
       }

        /// <summary>
        /// Gets the last computed total gradient with respect to σ, if available.
        /// </summary>
        public ConductivityDistribution? LastConductivityGradient => _last?.Gradient;

        /// <summary>
        /// Gets the last computed PDE adjoint contribution, available when
        /// <see cref="Config.ReturnComponents"/> is enabled.
        /// </summary>
        public IReadOnlyDictionary<int, double>? LastAdjointComponent =>
            _config.ReturnComponents ? _last?.GradientAdjointTerm : null;

        /// <summary>
        /// Gets the last computed conductivity-cost contribution, available when
        /// <see cref="Config.ReturnComponents"/> is enabled.
        /// </summary>
        public IReadOnlyDictionary<int, double>? LastCostComponent =>
            _config.ReturnComponents ? _last?.GradientCostTerm : null;

        /// <inheritdoc />
        public double Evaluate(IDiscretization discretization, double[] measured, double[] simulated)
        {
            if (discretization == null) throw new ArgumentNullException(nameof(discretization));
            if (measured == null) throw new ArgumentNullException(nameof(measured));
            if (simulated == null) throw new ArgumentNullException(nameof(simulated));
            if (measured.Length != simulated.Length)
                throw new ArgumentException("Measured and simulated arrays must share the same length.");

            if (measured.Length == 0)
                return 0.0;

            var electrodes = discretization.GetElectrodes();
            if (electrodes.Count != measured.Length)
                throw new ArgumentException("Measured data size must match the electrode count.");

            var mesh = discretization.GetDiscretization();
            if (mesh is not FEMMesh fem)
                throw new NotSupportedException("ConductivityAwareW2Metric currently requires a FEM mesh discretization.");

            // (1) Smooth normalisation of electrode data.
            var simNorm = Normalize(simulated, _config.Kappa, _config.Epsilon);
            var measNorm = Normalize(measured, _config.Kappa, _config.Epsilon);
            var mu = simNorm.Normalized;
            var nu = measNorm.Normalized;

            // Determine electrode spatial positions.
            var electrodePositions = GetElectrodePositions(fem);
            var electrodeNodes = MapElectrodesToGraphNodes(fem, electrodePositions);

            // Build both geometric and conductivity-aware cost matrices.
            var geodesics = ComputeSoftDistancesAndOccupancies(fem, electrodeNodes);
            var costConductive = BuildCostMatrix(geodesics.Distances);
            var costGeometric = BuildGeometricCost(electrodePositions);

            bool physicsOnly = _config.UsePhysicsAwareOnly || _config.Alpha >= 1.0 - 1e-12;
            double alpha = physicsOnly ? 1.0 : Math.Clamp(_config.Alpha, 0.0, 1.0);

            var otPhysics = SolveOptimalTransport(costConductive, mu, nu);
            OptimalTransportResult? otGeo = null;
            if (!physicsOnly && alpha < 1.0)
                otGeo = SolveOptimalTransport(costGeometric, mu, nu);

            // Build misfit value (Eq. (4)).
            double physTerm = 0.5 * WeightedSum(costConductive, otPhysics.Plan);
            double value = physTerm;
            if (!physicsOnly && otGeo != null)
            {
                double geoTerm = 0.5 * WeightedSum(costGeometric, otGeo.Plan);
                value = 0.5 * ((1.0 - alpha) * 2.0 * geoTerm + alpha * 2.0 * physTerm);
            }

            // Average Kantorovich potentials according to the blend.
            var gMu = BlendPotentials(alpha, otPhysics.SourcePotential, otGeo?.SourcePotential);

            // R^T g_μ for the adjoint RHS (Eq. (1) VJP).
            var adjointSource = ApplyNormalizationVjp(simNorm, gMu);
            // OT physics-cost correction (Eq. (5)-(7)).
            var costTerm = ComputeCostGradient(fem, geodesics, otPhysics.Plan);
            _last = new EvaluationCache
            {
                Mu = mu,
                Nu = nu,
                GammaPhysics = otPhysics.Plan,
                GammaGeo = otGeo?.Plan,
                AlphaPhysics = otPhysics.SourcePotential,
                AlphaGeo = otGeo?.SourcePotential,
                SimNormalization = simNorm,
                Geodesics = geodesics,
                GMu = gMu,
                AdjointElectrodeSource = adjointSource,
                GradientAdjointTerm = null,
                GradientCostTerm = costTerm,
                Measured = (double[])measured.Clone(),
                Simulated = (double[])simulated.Clone(),
                Gradient = null
            };

            return value;
        }

        /// <inheritdoc />
        public double[] EvaluateAdjointSource(IDiscretization discretization, double[] measured, double[] simulated)
        {
            if (discretization == null) throw new ArgumentNullException(nameof(discretization));
            if (measured == null) throw new ArgumentNullException(nameof(measured));
            if (simulated == null) throw new ArgumentNullException(nameof(simulated));

            if (_last == null || !InputsMatch(_last, measured, simulated))
            {
                _ = Evaluate(discretization, measured, simulated);
            }

            return (double[])_last!.AdjointElectrodeSource.Clone();
        }

        private static bool InputsMatch(EvaluationCache cache, double[] measured, double[] simulated)
        {
            if (cache.Measured.Length != measured.Length || cache.Simulated.Length != simulated.Length)
                return false;

            var comparer = EqualityComparer<double>.Default;

            for (int i = 0; i < cache.Measured.Length; i++)
            {
                if (!comparer.Equals(cache.Measured[i], measured[i]))
                    return false;
            }

            for (int i = 0; i < cache.Simulated.Length; i++)
            {
                if (!comparer.Equals(cache.Simulated[i], simulated[i]))
                    return false;
            }

            return true;
        }

        /// <summary>
        /// Combines the cached conductivity-aware OT sensitivity with an externally computed
        /// adjoint potential to assemble the full gradient with respect to σ.
        /// Callers are expected to obtain the adjoint field by solving ∇·(σ∇λ) = -S^T R^T g_μ
        /// using the source returned by <see cref="EvaluateAdjointSource"/>.
        /// </summary>
        /// <param name="discretization">Discretization that produced the cached evaluation.</param>
        /// <param name="adjointPotential">Adjoint potential λ obtained from the PDE solver.</param>
        /// <returns>The combined conductivity gradient.</returns>
        public ConductivityDistribution AssembleTotalConductivityGradient(
            IDiscretization discretization,
            PotentialDistribution adjointPotential)
        {
            if (discretization == null) throw new ArgumentNullException(nameof(discretization));
            if (adjointPotential == null) throw new ArgumentNullException(nameof(adjointPotential));
            if (_last == null)
                throw new InvalidOperationException("Evaluate must be called before assembling gradients.");

            var mesh = discretization.GetDiscretization();
            if (mesh is not FEMMesh fem)
                throw new NotSupportedException("ConductivityAwareW2Metric currently requires a FEM mesh discretization.");

            /*
             * (7) Final gradient assembly:
             *     ∂J/∂σ(x) = -∇ϕ(x)·∇λ(x) + ½ Σ₍ᵢⱼ₎ Γ*_{ij} ∂Cσ(i,j)/∂σ(x).
             * The adjoint contribution uses the supplied λ, while the transport-cost
             * correction reuses the cached soft-geodesic sensitivities from Evaluate().
             */
            var phi = fem.GetPotentialDistribution() ??
                      throw new InvalidOperationException("Forward potential distribution is missing on the FEM mesh.");

            var adjointTerm = ComputeAdjointConductivityGradient(fem, phi, adjointPotential);
            var costTerm = _last.GradientCostTerm ?? ComputeCostGradient(fem, _last.Geodesics, _last.GammaPhysics);

            _last.GradientAdjointTerm = adjointTerm;

            var totalGradient = MergeGradientComponents(fem, adjointTerm, costTerm);
            _last.Gradient = totalGradient;
            return totalGradient;
        }

        /// <summary>
        /// (1) Smooth normalisation map using the softplus temperature κ and mass floor ε.
        /// Returns both the normalised histogram and auxiliary data for the VJP.
        /// </summary>
        internal static NormalizationCache Normalize(double[] raw, double kappa, double epsilon)
        {
            if (raw == null) throw new ArgumentNullException(nameof(raw));
            if (raw.Length == 0)
                return new NormalizationCache(Array.Empty<double>(), Array.Empty<double>(), Array.Empty<double>(), Array.Empty<double>(), Array.Empty<double>(), 0, 0.0);

            double[] shifted = new double[raw.Length];
            double[] softplus = new double[raw.Length];
            double[] sigmoid = new double[raw.Length];
            double[] normalized = new double[raw.Length];

            double min = raw[0];
            int minIdx = 0;
            for (int i = 1; i < raw.Length; i++)
            {
                if (raw[i] < min)
                {
                    min = raw[i];
                    minIdx = i;
                }
            }

            double sum = 0.0;
            for (int i = 0; i < raw.Length; i++)
            {
                double val = raw[i] - min;
                shifted[i] = val;
                double kx = kappa * val;
                double sp = Softplus(kx) / kappa;
                softplus[i] = sp;
                double sig = Sigmoid(kx);
                sigmoid[i] = sig;
                double mass = sp + epsilon;
                normalized[i] = mass;
                sum += mass;
            }

            if (sum <= 0.0)
                throw new InvalidOperationException("Normalisation resulted in zero total mass.");

            for (int i = 0; i < normalized.Length; i++)
                normalized[i] /= sum;

            return new NormalizationCache(raw, shifted, softplus, sigmoid, normalized, minIdx, sum);
        }

        /// <summary>
        /// Vector-Jacobian product R^T g_μ for the smooth normalisation map in Eq. (1).
        /// </summary>
        internal static double[] ApplyNormalizationVjp(in NormalizationCache cache, double[] gMu)
        {
            if (gMu == null) throw new ArgumentNullException(nameof(gMu));
            if (cache.Normalized.Length != gMu.Length)
                throw new ArgumentException("Gradient vector length mismatch.");

            int n = gMu.Length;
            double[] result = new double[n];

            double sumSigmoid = 0.0;
            double sumSigmoidWeighted = 0.0;
            double meanWeighted = 0.0;

            for (int i = 0; i < n; i++)
            {
                sumSigmoid += cache.Sigmoid[i];
                sumSigmoidWeighted += gMu[i] * cache.Sigmoid[i];
                meanWeighted += gMu[i] * cache.Normalized[i];
            }
            meanWeighted *= cache.Sum;

            double sum = cache.Sum;
            int minIdx = cache.MinIndex;

            for (int k = 0; k < n; k++)
            {
                double a = gMu[k] * cache.Sigmoid[k];
                if (k == minIdx)
                    a -= sumSigmoidWeighted;

                double b = cache.Sigmoid[k];
                if (k == minIdx)
                    b -= sumSigmoid;

                double term1 = sum * a;
                double term2 = meanWeighted * b;
                result[k] = (term1 - term2) / (sum * sum);
            }

            return result;
        }
        private static double Softplus(double x)
        {
            if (x > 50) // avoid overflow
                return x;
            if (x < -50)
                return Math.Exp(x);
            return Log1p(Math.Exp(x));
        }

        private static double Log1p(double x)
        {
            // For very small x, use series expansion to avoid loss of precision
            if (Math.Abs(x) < 1e-4)
                return x - x * x / 2.0 + x * x * x / 3.0;
            return Math.Log(1.0 + x);
        }

        private static double Sigmoid(double x)
        {
            if (x >= 0)
            {
                double e = Math.Exp(-x);
                return 1.0 / (1.0 + e);
            }
            else
            {
                double e = Math.Exp(x);
                return e / (1.0 + e);
            }
        }

        private static double[,] BuildCostMatrix(double[,] distances)
        {
            int m = distances.GetLength(0);
            int n = distances.GetLength(1);
            var cost = new double[m, n];
            for (int i = 0; i < m; i++)
                for (int j = 0; j < n; j++)
                {
                    double d = distances[i, j];
                    cost[i, j] = d * d;
                }
            return cost;
        }

        private static double[,] BuildGeometricCost(IReadOnlyList<(double x, double y)> electrodePositions)
        {
            int m = electrodePositions.Count;
            var cost = new double[m, m];
            for (int i = 0; i < m; i++)
            {
                for (int j = 0; j < m; j++)
                {
                    double dx = electrodePositions[i].x - electrodePositions[j].x;
                    double dy = electrodePositions[i].y - electrodePositions[j].y;
                    double d = Math.Sqrt(dx * dx + dy * dy);
                    cost[i, j] = d * d;
                }
            }
            return cost;
        }

        private sealed record OptimalTransportResult(double[,] Plan, double[] SourcePotential, double[] TargetPotential);

        private static OptimalTransportResult SolveOptimalTransport(double[,] cost, double[] mu, double[] nu)
        {
            var plan = SolveOptimalTransportPrimal(cost, mu, nu, out var alpha, out var beta);
            return new OptimalTransportResult(plan, alpha, beta);
        }
        
        private static Variable MakeNonNegativeVariable(Solver solver, string name)
        {
            return solver.MakeNumVar(0.0, double.PositiveInfinity, name);
        }
        /// <summary>
        /// (3) Primal LP: min_Γ Σ Cᵢⱼ Γᵢⱼ subject to row/column marginals.
        /// </summary>
        internal static double[,] SolveOptimalTransportPrimal(double[,] cost, double[] mu, double[] nu)
            => SolveOptimalTransportPrimal(cost, mu, nu, out _, out _);

        private static double[,] SolveOptimalTransportPrimal(double[,] cost, double[] mu, double[] nu,
            out double[] sourcePotential, out double[] targetPotential)
        {
            int m = mu.Length;
            int n = nu.Length;
            var solver = Solver.CreateSolver("GLOP") ?? throw new InvalidOperationException("OR-Tools GLOP solver unavailable.");

            var planVar = new Variable[m, n];
            for (int i = 0; i < m; i++)
                for (int j = 0; j < n; j++)
                    planVar[i, j] = MakeNonNegativeVariable(solver, $"P[{i},{j}]");

            var row = new Constraint[m];
            for (int i = 0; i < m; i++)
            {
                row[i] = solver.MakeConstraint(mu[i], mu[i], $"row[{i}]");
                for (int j = 0; j < n; j++)
                    row[i].SetCoefficient(planVar[i, j], 1.0);
            }

            var col = new Constraint[n];
            for (int j = 0; j < n; j++)
            {
                col[j] = solver.MakeConstraint(nu[j], nu[j], $"col[{j}]");
                for (int i = 0; i < m; i++)
                    col[j].SetCoefficient(planVar[i, j], 1.0);
            }

            var objective = solver.Objective();
            for (int i = 0; i < m; i++)
                for (int j = 0; j < n; j++)
                    objective.SetCoefficient(planVar[i, j], cost[i, j]);
            objective.SetMinimization();

            var status = solver.Solve();
            if (status != Solver.ResultStatus.OPTIMAL)
                throw new InvalidOperationException($"Optimal transport primal LP failed with status {status}.");

            var planMatrix = new double[m, n];
            for (int i = 0; i < m; i++)
                for (int j = 0; j < n; j++)
                    planMatrix[i, j] = planVar[i, j].SolutionValue();

            sourcePotential = new double[m];
            targetPotential = new double[n];
            for (int i = 0; i < m; i++)
                sourcePotential[i] = row[i].DualValue();
            for (int j = 0; j < n; j++)
                targetPotential[j] = col[j].DualValue();

            return planMatrix;
        }

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

        private double[] BlendPotentials(double alpha, double[] phys, double[]? geo)
        {
            int n = phys.Length;
            double[] result = new double[n];
            double physWeight = alpha;
            double geoWeight = 1.0 - alpha;
            for (int i = 0; i < n; i++)
            {
                double value = physWeight * phys[i];
                if (geo != null)
                    value += geoWeight * geo[i];
                result[i] = 0.5 * value; // Eq. (5): g_μ = ½ α⋆
            }
            return result;
        }

        private static IReadOnlyList<(double x, double y)> GetElectrodePositions(FEMMesh mesh)
        {
            var vertices = mesh.GetVertices().ToDictionary(v => v.GlobalId);
            var electrodes = mesh.GetElectrodes().Cast<FEMElectrode>().ToList();
            var positions = new List<(double x, double y)>(electrodes.Count);

            foreach (var electrode in electrodes)
            {
                if (electrode.FEMVertexIds.Count == 0)
                {
                    if (!vertices.TryGetValue(electrode.MeshId, out var v))
                        throw new InvalidOperationException($"Electrode {electrode.Id} does not reference any FEM vertex.");
                    positions.Add((v.X, v.Y));
                    continue;
                }

                double sx = 0.0, sy = 0.0;
                foreach (var id in electrode.FEMVertexIds)
                {
                    if (!vertices.TryGetValue(id, out var v))
                        throw new InvalidOperationException($"Electrode {electrode.Id} references missing FEM vertex {id}.");
                    sx += v.X;
                    sy += v.Y;
                }
                double inv = 1.0 / electrode.FEMVertexIds.Count;
                positions.Add((sx * inv, sy * inv));
            }

            return positions;
        }

        /// <summary>
        /// Exposes the electrode-to-graph mapping for unit tests.
        /// </summary>
        internal int[] DebugElectrodeNodeMapping(FEMMesh mesh)
        {
            var positions = GetElectrodePositions(mesh);
            return MapElectrodesToGraphNodes(mesh, positions);
        }

        /// <summary>
        /// Exposes the soft geodesic solver for unit tests.
        /// </summary>
        internal SoftGeodesicResult DebugComputeSoftGeodesics(FEMMesh mesh)
        {
            var nodes = DebugElectrodeNodeMapping(mesh);
            return ComputeSoftDistancesAndOccupancies(mesh, nodes);
        }

        private static int[] MapElectrodesToGraphNodes(FEMMesh mesh, IReadOnlyList<(double x, double y)> electrodePositions)
        {
            var graph = mesh.ToGraph();
            var vertices = graph.Vertices;
            int nodeCount = vertices.Count;

            int[] mapping = new int[electrodePositions.Count];
            for (int i = 0; i < electrodePositions.Count; i++)
            {
                double ex = electrodePositions[i].x;
                double ey = electrodePositions[i].y;
                double best = double.PositiveInfinity;
                int bestIdx = 0;
                for (int idx = 0; idx < nodeCount; idx++)
                {
                    var v = vertices[idx];
                    double dx = ex - v.X;
                    double dy = ey - v.Y;
                    double dist2 = dx * dx + dy * dy;
                    if (dist2 < best)
                    {
                        best = dist2;
                        bestIdx = idx;
                    }
                }
                mapping[i] = bestIdx;
            }
            return mapping;
        }

        private SoftGeodesicResult ComputeSoftDistancesAndOccupancies(FEMMesh mesh, int[] electrodeNodes)
        {
            // Build graph data from the discretization.
            var graph = mesh.ToGraph();
            int nodeCount = graph.Vertices.Count;
            var nodeIndex = new Dictionary<int, int>(nodeCount);
            for (int i = 0; i < nodeCount; i++)
                nodeIndex[graph.Vertices[i].GlobalId] = i;

            var sigmaField = mesh.ConductivityDistribution.Conductivities;
            var edges = new List<EdgeInfo>(graph.Edges.Count);
            var adjacency = new List<DirectedEdge>[nodeCount];
            for (int i = 0; i < nodeCount; i++) adjacency[i] = new List<DirectedEdge>();

            for (int ei = 0; ei < graph.Edges.Count; ei++)
            {
                var e = graph.Edges[ei];
                int u = nodeIndex[e.Vertices[0].GlobalId];
                int v = nodeIndex[e.Vertices[1].GlobalId];
                if (u == v) continue;
                double dx = e.Vertices[0].X - e.Vertices[1].X;
                double dy = e.Vertices[0].Y - e.Vertices[1].Y;
                double length = Math.Sqrt(dx * dx + dy * dy);
                if (length <= 0.0)
                    length = 1e-12;

                double sigU = sigmaField.TryGetValue(e.Vertices[0].GlobalId, out var sU) ? sU : 1.0;
                double sigV = sigmaField.TryGetValue(e.Vertices[1].GlobalId, out var sV) ? sV : 1.0;
                double sigmaEdge = 0.5 * (Math.Clamp(sigU, _config.SigmaFloor, _config.SigmaCeiling) +
                                          Math.Clamp(sigV, _config.SigmaFloor, _config.SigmaCeiling));
                double cost = length * Math.Pow(sigmaEdge, -_config.Beta);

                edges.Add(new EdgeInfo(ei, u, v, length, e.Vertices[0].GlobalId, e.Vertices[1].GlobalId, sigmaEdge, cost));
                adjacency[u].Add(new DirectedEdge(ei, u, v, cost));
                adjacency[v].Add(new DirectedEdge(ei, v, u, cost));
            }

            int m = electrodeNodes.Length;
            var distances = new double[m, m];
            var occupancies = new Dictionary<(int source, int target), double[]>();

            for (int targetIdx = 0; targetIdx < m; targetIdx++)
            {
                int targetNode = electrodeNodes[targetIdx];
                var softDistances = SolveSoftDistances(nodeCount, adjacency, targetNode);
                var transitions = BuildTransitions(nodeCount, adjacency, softDistances, targetNode);
                var (lu, indexMap) = FactoriseTransitions(transitions, targetNode);

                for (int sourceIdx = 0; sourceIdx < m; sourceIdx++)
                {
                    int sourceNode = electrodeNodes[sourceIdx];
                    double distance = softDistances[sourceNode];
                    distances[sourceIdx, targetIdx] = distance;

                    var rho = ComputeEdgeOccupancies(nodeCount, edges, transitions, lu, indexMap, sourceNode, targetNode);
                    occupancies[(sourceIdx, targetIdx)] = rho;
                }
            }

            return new SoftGeodesicResult(distances, occupancies, edges);
        }

        /// <summary>
        /// (2) Soft Bellman solver implementing d_τ(x) = -τ log Σ exp(-(c(x,y)+d_τ(y))/τ).
        /// </summary>
        private double[] SolveSoftDistances(int nodeCount, IList<DirectedEdge>[] adjacency, int target)
        {
            double[] d = new double[nodeCount];
            for (int i = 0; i < nodeCount; i++)
                d[i] = (i == target) ? 0.0 : 0.0;

            double tau = Math.Max(_config.Tau, 1e-6);
            double damping = Math.Clamp(_config.BellmanDamping, 0.0, 1.0);

            for (int iter = 0; iter < _config.MaxBellmanIterations; iter++)
            {
                double maxDelta = 0.0;
                for (int x = 0; x < nodeCount; x++)
                {
                    if (x == target) continue;
                    var neighbors = adjacency[x];
                    if (neighbors.Count == 0) continue;

                    double maxVal = double.NegativeInfinity;
                    for (int k = 0; k < neighbors.Count; k++)
                    {
                        double val = -(neighbors[k].Cost + d[neighbors[k].To]) / tau;
                        if (val > maxVal) maxVal = val;
                    }

                    double sum = 0.0;
                    for (int k = 0; k < neighbors.Count; k++)
                    {
                        double val = -(neighbors[k].Cost + d[neighbors[k].To]) / tau;
                        sum += Math.Exp(val - maxVal);
                    }
                    double logSum = maxVal + Math.Log(sum);
                    double candidate = -tau * logSum;
                    double updated = damping * candidate + (1.0 - damping) * d[x];
                    double delta = Math.Abs(updated - d[x]);
                    if (delta > maxDelta) maxDelta = delta;
                    d[x] = updated;
                }

                if (maxDelta < _config.BellmanTolerance)
                    break;
            }

            return d;
        }

        private static List<Transition>[] BuildTransitions(int nodeCount, IList<DirectedEdge>[] adjacency, double[] d, int target)
        {
            var transitions = new List<Transition>[nodeCount];
            for (int i = 0; i < nodeCount; i++)
            {
                transitions[i] = new List<Transition>();
                if (i == target) continue;
                var neighbors = adjacency[i];
                if (neighbors.Count == 0) continue;

                double maxVal = double.NegativeInfinity;
                for (int k = 0; k < neighbors.Count; k++)
                {
                    double val = -(neighbors[k].Cost + d[neighbors[k].To]);
                    if (val > maxVal) maxVal = val;
                }

                double denom = 0.0;
                for (int k = 0; k < neighbors.Count; k++)
                {
                    double val = -(neighbors[k].Cost + d[neighbors[k].To]);
                    denom += Math.Exp(val - maxVal);
                }
                double logDenom = maxVal + Math.Log(denom);

                for (int k = 0; k < neighbors.Count; k++)
                {
                    double val = -(neighbors[k].Cost + d[neighbors[k].To]);
                    double prob = Math.Exp(val - logDenom);
                    transitions[i].Add(new Transition(neighbors[k].EdgeIndex, neighbors[k].To, prob));
                }
            }

            return transitions;
        }

        private static (LU<double> lu, Dictionary<int, int> indexMap) FactoriseTransitions(List<Transition>[] transitions, int target)
        {
            int nodeCount = transitions.Length;
            var indexMap = new Dictionary<int, int>();
            int idx = 0;
            for (int i = 0; i < nodeCount; i++)
            {
                if (i == target) continue;
                indexMap[i] = idx++;
            }

            var matrix = DenseMatrix.Create(idx, idx, 0.0);
            for (int i = 0; i < nodeCount; i++)
            {
                if (i == target) continue;
                int row = indexMap[i];
                matrix[row, row] = 1.0;
                foreach (var t in transitions[i])
                {
                    if (!indexMap.TryGetValue(t.To, out int col))
                        continue; // transition to absorbing state
                    matrix[row, col] -= t.Probability;
                }
            }

            var lu = matrix.LU();
            return (lu, indexMap);
        }

        private static double[] ComputeEdgeOccupancies(int nodeCount,
                                                        IReadOnlyList<EdgeInfo> edges,
                                                        List<Transition>[] transitions,
                                                        LU<double> lu,
                                                        Dictionary<int, int> indexMap,
                                                        int sourceNode,
                                                        int targetNode)
        {
            double[] rho = new double[edges.Count];
            if (sourceNode == targetNode)
                return rho;
            if (!indexMap.TryGetValue(sourceNode, out int sourceIdx))
                return rho;

            var b = DenseVector.Create(indexMap.Count, 0.0);
            b[sourceIdx] = 1.0;
            var eta = lu.Solve(b);

            double[] visitCounts = new double[nodeCount];
            foreach (var kv in indexMap)
                visitCounts[kv.Key] = eta[kv.Value];

            foreach (var kv in indexMap)
            {
                int node = kv.Key;
                double visits = visitCounts[node];
                foreach (var t in transitions[node])
                {
                    int edgeIndex = t.EdgeIndex;
                    double contribution = visits * t.Probability;
                    rho[edgeIndex] += contribution;
                }
            }

            return rho;
        }

        private Dictionary<int, double> ComputeCostGradient(FEMMesh mesh, SoftGeodesicResult geodesics, double[,] gamma)
        {
            var gradient = new Dictionary<int, double>();

            foreach (var edge in geodesics.EdgeInfos)
            {
                if (!gradient.ContainsKey(edge.ElementU)) gradient[edge.ElementU] = 0.0;
                if (!gradient.ContainsKey(edge.ElementV)) gradient[edge.ElementV] = 0.0;
            }

            foreach (var kv in geodesics.Occupancies)
            {
                int source = kv.Key.source;
                int target = kv.Key.target;
                double distance = geodesics.Distances[source, target];
                double gammaWeight = gamma[source, target];
                if (gammaWeight == 0.0 || distance == 0.0)
                    continue;

                var rho = kv.Value;
                for (int ei = 0; ei < geodesics.EdgeInfos.Count; ei++)
                {
                    double occupancy = rho[ei];
                    if (occupancy == 0.0) continue;
                    var edge = geodesics.EdgeInfos[ei];
                    double sigma = Math.Max(edge.Sigma, 1e-12);
                    double factor = -_config.Beta * edge.Length * Math.Pow(sigma, -_config.Beta - 1);
                    double contribution = 0.5 * gammaWeight * distance * occupancy * factor;

                    gradient[edge.ElementU] += contribution;
                    gradient[edge.ElementV] += contribution;
                }
            }

            return gradient;
        }

        private static Dictionary<int, double> ComputeAdjointConductivityGradient(FEMMesh mesh, PotentialDistribution phi, PotentialDistribution lambda)
        {
            var result = new Dictionary<int, double>();
            foreach (var element in mesh.ElementsTyped)
            {
                var gradPhi = ComputeElementGradient(element, phi);
                var gradLambda = ComputeElementGradient(element, lambda);
                double dot = gradPhi.dx * gradLambda.dx + gradPhi.dy * gradLambda.dy;
                result[element.Id] = -dot * element.Area;
            }
            return result;
        }

        private static (double dx, double dy) ComputeElementGradient(FEMElement element, PotentialDistribution potential)
        {
            double gx = 0.0, gy = 0.0;
            for (int i = 0; i < 3; i++)
            {
                int nodeId = element.Vertices[i].GlobalId;
                double value = potential.Potentials.TryGetValue(nodeId, out var val) ? val : 0.0;
                gx += value * element.GradPhi[i][0];
                gy += value * element.GradPhi[i][1];
            }
            return (gx, gy);
        }

        private static ConductivityDistribution MergeGradientComponents(FEMMesh mesh,
                                                                         Dictionary<int, double>? adjoint,
                                                                         Dictionary<int, double> cost)
        {
            var merged = new Dictionary<int, double>(mesh.ElementsTyped.Count);
            foreach (var element in mesh.ElementsTyped)
            {
                double adj = 0.0;
                if (adjoint != null && adjoint.TryGetValue(element.Id, out var a))
                    adj = a;
                double c = cost.TryGetValue(element.Id, out var v) ? v : 0.0;
                merged[element.Id] = adj + c;
            }

            return new ConductivityDistribution(merged);
        }

        internal static Dictionary<int, double> LiftElectrodeSourceToNodes(FEMMesh mesh, double[] electrodeSource)

        {
            var map = new Dictionary<int, double>();
            var electrodes = mesh.GetElectrodes().Cast<FEMElectrode>().ToList();
            if (electrodeSource.Length != electrodes.Count)
                throw new ArgumentException("Adjoint source vector size must match electrode count.");

            for (int i = 0; i < electrodes.Count; i++)
            {
                var electrode = electrodes[i];
                double value = electrodeSource[i];
                if (electrode.FEMVertexIds.Count == 0)
                {
                    int node = electrode.MeshId;
                    map[node] = map.TryGetValue(node, out var existing) ? existing + value : value;
                    continue;
                }

                double share = value / electrode.FEMVertexIds.Count;
                foreach (var node in electrode.FEMVertexIds)
                    map[node] = map.TryGetValue(node, out var existing) ? existing + share : share;
            }

            return map;
        }
    }
}
