using Google.OrTools.LinearSolver;
using MathNet.Numerics.LinearAlgebra;
using MathNet.Numerics.LinearAlgebra.Double;
using Utility.Classes.Discretizer;
using Utility.Classes.Discretizer.FiniteElementMesh;
using Utility.Classes.ReconstructionParameters;

namespace Utility.Classes.Reconstruction.ErrorMetrics
{
    /// <summary>
    /// Implements a Wasserstein-2 misfit whose ground cost is the Dirichlet
    /// energy induced by the current conductivity distribution. The optimal
    /// transport problem is solved with the same LP workflow used by the other
    /// Wasserstein metrics; only the ground cost differs.
    /// </summary>
    public sealed class EnergyBasedWasserstein2Metric : IErrorMetric
    {
        /// <summary>
        /// Small numerical tolerance to avoid division by zero and handle degenerate cases.
        /// </summary>
        private const double Tiny = 1e-12;

        /// <summary>
        /// Cache for the last computation to avoid redundant calculations when
        /// evaluating both the objective and adjoint source with the same data.
        /// </summary>
        private CachedResult? _last;

        // Geometry-dependent FEM bookkeeping is reused between solves. Only the
        // conductivity-dependent stiffness assembly is refreshed per evaluation.
        private readonly object _energyOperatorLock = new();
        private FEMMesh? _cachedEnergyMesh;
        private int[]? _cachedElectrodeIds;
        private ElectrodeEnergyOperator? _cachedEnergyOperator;

        /// <inheritdoc />
        public double Evaluate(IDiscretization discretization, double[] measured, double[] simulated)
        {
            var result = Solve(discretization, measured, simulated);
            _last = result;
            return result.Cost;
        }

        /// <inheritdoc />
        public double[] EvaluateAdjointSource(IDiscretization discretization, double[] measured, double[] simulated)
        {
            if (_last != null && _last.Matches(measured, simulated))
                return _last.Adjoint;

            var result = Solve(discretization, measured, simulated);
            _last = result;
            return result.Adjoint;
        }

        /// <summary>
        /// Runs the full energy-based Wasserstein solve.
        /// </summary>
        private CachedResult Solve(IDiscretization discretization, double[] measured, double[] simulated)
        {
            if (discretization is null) throw new ArgumentNullException(nameof(discretization));
            if (measured is null) throw new ArgumentNullException(nameof(measured));
            if (simulated is null) throw new ArgumentNullException(nameof(simulated));

            if (measured.Length != simulated.Length)
                throw new ArgumentException("Measured and simulated arrays must have the same length.");

            if (discretization.GetDiscretization() is not FEMMesh fem)
                throw new NotSupportedException("EnergyBasedWasserstein2Metric currently requires a FEM mesh discretization.");

            var electrodes = fem.GetElectrodes();
            if (electrodes.Count != measured.Length)
                throw new ArgumentException("Data length must match the number of electrodes in the discretization.");

            var include = new List<int>(measured.Length);
            for (int i = 0; i < measured.Length; i++)
            {
                if (!double.IsNaN(measured[i]))
                    include.Add(i);
            }

            if (include.Count == 0)
                return CachedResult.Zero(measured, simulated);

            var muRaw = ExtractHistogram(simulated, include);
            var nuRaw = ExtractHistogram(measured, include);

            var mu = NormalizeHistogram(muRaw);
            var nu = NormalizeHistogram(nuRaw);
            if (mu.IsDegenerate || nu.IsDegenerate)
                return CachedResult.Zero(measured, simulated);

            var energy = GetOrCreateEnergyOperator(fem);
            var cost = energy.BuildCostMatrix(include);

            var transport = SolveOptimalTransport(cost, mu.Values, nu.Values);
            double loss = transport.Objective;

            var sourcePotential = (double[])transport.Alpha.Clone();
            double weightedMean = 0.0;
            for (int i = 0; i < sourcePotential.Length; i++)
                weightedMean += sourcePotential[i] * mu.Values[i];

            double invMass = 1.0 / mu.TotalMass;
            var sourceGradient = new double[sourcePotential.Length];
            for (int i = 0; i < sourceGradient.Length; i++)
                sourceGradient[i] = (sourcePotential[i] - weightedMean) * invMass;

            var adjointFull = new double[measured.Length];
            for (int i = 0; i < include.Count; i++)
                adjointFull[include[i]] = sourceGradient[i];

            return new CachedResult(measured,
                                    simulated,
                                    loss,
                                    adjointFull,
                                    include.ToArray(),
                                    sourcePotential,
                                    sourceGradient,
                                    transport.Plan);
        }

        private ElectrodeEnergyOperator GetOrCreateEnergyOperator(FEMMesh mesh)
        {
            lock (_energyOperatorLock)
            {
                bool reuse = _cachedEnergyOperator != null &&
                             ReferenceEquals(_cachedEnergyMesh, mesh) &&
                             MatchesElectrodeOrdering(mesh);

                if (reuse)
                    return _cachedEnergyOperator!;

                _cachedEnergyMesh = mesh;
                _cachedElectrodeIds = mesh.ElectrodesTyped.Select(e => e.Id).ToArray();
                _cachedEnergyOperator = new ElectrodeEnergyOperator(mesh);
                return _cachedEnergyOperator;
            }
        }

        private bool MatchesElectrodeOrdering(FEMMesh mesh)
        {
            if (_cachedElectrodeIds == null || _cachedElectrodeIds.Length != mesh.ElectrodesTyped.Count)
                return false;

            for (int i = 0; i < mesh.ElectrodesTyped.Count; i++)
            {
                if (_cachedElectrodeIds[i] != mesh.ElectrodesTyped[i].Id)
                    return false;
            }

            return true;
        }

        private static Histogram NormalizeHistogram(double[] raw)
        {
            double[] values = (double[])raw.Clone();
            double sum = 0.0;

            for (int i = 0; i < values.Length; i++)
            {
                double v = values[i];
                if (!double.IsFinite(v))
                    v = 0.0;
                if (v < 0.0)
                    v = 0.0;
                values[i] = v;
                sum += v;
            }

            if (sum <= Tiny)
                return Histogram.Degenerate(values);

            for (int i = 0; i < values.Length; i++)
                values[i] /= sum;

            return new Histogram(values, false, sum);
        }

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
        /// Solves the balanced discrete optimal transport LP. Costs are normalized
        /// before entering GLOP so the simplex basis stays numerically well-scaled
        /// during long reconstruction runs. Dual variables are rescaled back to the
        /// original transport-cost units afterwards.
        /// </summary>
        private static OptimalTransportSolution SolveOptimalTransport(double[,] cost, double[] source, double[] target)
        {
            int m = source.Length;
            int n = target.Length;
            double costScale = DetermineCostScale(cost);

            using var solver = Solver.CreateSolver("GLOP")
                               ?? throw new InvalidOperationException("OR-Tools LP solver 'GLOP' not available.");

            var plan = new Variable[m, n];
            var row = new Constraint[m];
            var col = new Constraint[n];

            for (int i = 0; i < m; i++)
                row[i] = solver.MakeConstraint(source[i], source[i], $"row[{i}]");
            for (int j = 0; j < n; j++)
                col[j] = solver.MakeConstraint(target[j], target[j], $"col[{j}]");

            var objective = solver.Objective();
            for (int i = 0; i < m; i++)
            {
                for (int j = 0; j < n; j++)
                {
                    plan[i, j] = solver.MakeNumVar(0.0, double.PositiveInfinity, $"P[{i},{j}]");
                    row[i].SetCoefficient(plan[i, j], 1.0);
                    col[j].SetCoefficient(plan[i, j], 1.0);
                    objective.SetCoefficient(plan[i, j], cost[i, j] / costScale);
                }
            }

            objective.SetMinimization();
            var status = solver.Solve();
            if (status != Solver.ResultStatus.OPTIMAL)
                throw new InvalidOperationException($"Optimal transport LP failed with status {status}.");

            var planMatrix = new double[m, n];
            for (int i = 0; i < m; i++)
                for (int j = 0; j < n; j++)
                    planMatrix[i, j] = plan[i, j].SolutionValue();

            double objectiveValue = WeightedSum(cost, planMatrix);

            var alpha = new double[m];
            var beta = new double[n];
            for (int i = 0; i < m; i++)
                alpha[i] = row[i].DualValue() * costScale;
            for (int j = 0; j < n; j++)
                beta[j] = col[j].DualValue() * costScale;

            return new OptimalTransportSolution(planMatrix, alpha, beta, objectiveValue);
        }

        private static double DetermineCostScale(double[,] cost)
        {
            double max = 0.0;
            int rows = cost.GetLength(0);
            int cols = cost.GetLength(1);
            for (int i = 0; i < rows; i++)
            {
                for (int j = 0; j < cols; j++)
                {
                    double value = Math.Abs(cost[i, j]);
                    if (double.IsFinite(value) && value > max)
                        max = value;
                }
            }

            return max > Tiny ? max : 1.0;
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

        private readonly struct Histogram
        {
            public Histogram(double[] values, bool degenerate, double totalMass)
            {
                Values = values;
                IsDegenerate = degenerate;
                TotalMass = totalMass;
            }

            public double[] Values { get; }
            public bool IsDegenerate { get; }
            public double TotalMass { get; }

            public static Histogram Degenerate(double[] values) => new(values, true, 0.0);
        }

        private sealed class CachedResult
        {
            public CachedResult(double[] measured,
                                double[] simulated,
                                double cost,
                                double[] adjoint,
                                int[] included,
                                double[] sourcePotential,
                                double[] sourceGradient,
                                double[,] plan)
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

            public double[] Measured { get; }
            public double[] Simulated { get; }
            public double Cost { get; }
            public double[] Adjoint { get; }
            public int[] Included { get; }
            public double[] SourcePotential { get; }
            public double[] SourceGradient { get; }
            public double[,] Plan { get; }

            public bool Matches(double[] measured, double[] simulated)
                => ReferenceEquals(Measured, measured) && ReferenceEquals(Simulated, simulated);

            public static CachedResult Zero(double[] measured, double[] simulated)
            {
                var zeros = new double[measured.Length];
                return new CachedResult(measured,
                                        simulated,
                                        0.0,
                                        zeros,
                                        Array.Empty<int>(),
                                        Array.Empty<double>(),
                                        Array.Empty<double>(),
                                        new double[0, 0]);
            }
        }

        private sealed record OptimalTransportSolution(double[,] Plan, double[] Alpha, double[] Beta, double Objective);

        /// <summary>
        /// Builds conductivity-dependent Dirichlet-energy costs between finite electrodes.
        /// The exact paper-level object would live in an operator assembled from the full
        /// complete electrode model. Here we keep the same mathematics but cache every
        /// topology-only quantity so repeated solves do not keep reallocating large dense
        /// arrays or refactoring geometry-independent data.
        /// </summary>
        private sealed class ElectrodeEnergyOperator
        {
            private const double ContactImpedanceFloor = 1e-12;

            private readonly int _electrodeCount;
            private readonly int _gaugeIndex;
            private readonly int[] _fullToReducedIndex;
            private readonly IReadOnlyList<FEMElement> _elements;
            private readonly ElementContribution[] _elementContributions;
            private readonly DenseMatrix _coupling;
            private readonly Matrix<double> _couplingTranspose;
            private readonly DenseMatrix _diag;
            private readonly double[,] _fixedStiffness;
            private readonly double[,] _workingStiffness;
            private readonly Dictionary<int, double[,]> _costScratch = new();

            public ElectrodeEnergyOperator(FEMMesh mesh)
            {
                var electrodes = mesh.ElectrodesTyped.ToList();
                _elements = mesh.ElementsTyped;
                _electrodeCount = electrodes.Count;
                _gaugeIndex = Math.Max(0, _electrodeCount - 1);
                _fullToReducedIndex = BuildReducedIndexLookup(_electrodeCount, _gaugeIndex);

                if (_electrodeCount == 0)
                {
                    _elementContributions = Array.Empty<ElementContribution>();
                    _coupling = DenseMatrix.Create(0, 0, 0.0);
                    _couplingTranspose = _coupling.Transpose();
                    _diag = DenseMatrix.Create(0, 0, 0.0);
                    _fixedStiffness = new double[0, 0];
                    _workingStiffness = new double[0, 0];
                    return;
                }

                int nodeCount = mesh.Vertices.Count;
                _fixedStiffness = new double[nodeCount, nodeCount];
                _workingStiffness = new double[nodeCount, nodeCount];

                var vertexIndexLookup = new Dictionary<int, int>(nodeCount);
                for (int i = 0; i < mesh.Vertices.Count; i++)
                    vertexIndexLookup[mesh.Vertices[i].GlobalId] = i;

                _elementContributions = new ElementContribution[_elements.Count];
                for (int i = 0; i < _elements.Count; i++)
                    _elementContributions[i] = ElementContribution.Create(_elements[i], vertexIndexLookup);

                double[,] couplingArray = new double[_electrodeCount, nodeCount];
                double[,] diagArray = new double[_electrodeCount, _electrodeCount];

                for (int ell = 0; ell < electrodes.Count; ell++)
                {
                    var electrode = electrodes[ell];
                    double z = electrode.ZContact;
                    double invZ = 1.0 / Math.Max(z, ContactImpedanceFloor);
                    double length = electrode.Length;
                    int nodeMultiplicity = Math.Max(1, electrode.FEMVertexIds?.Count ?? 0);
                    double h = length / nodeMultiplicity;

                    if (electrode.FEMVertexIds != null && electrode.FEMVertexIds.Count > 0)
                    {
                        foreach (int vertexId in electrode.FEMVertexIds)
                        {
                            if (!vertexIndexLookup.TryGetValue(vertexId, out int localVertex))
                                continue;

                            _fixedStiffness[localVertex, localVertex] += invZ * h;
                            couplingArray[ell, localVertex] += invZ * h;
                        }
                    }
                    else if (vertexIndexLookup.TryGetValue(electrode.MeshId, out int localVertex))
                    {
                        _fixedStiffness[localVertex, localVertex] += invZ * h;
                        couplingArray[ell, localVertex] += invZ * h;
                    }

                    diagArray[ell, ell] = length * invZ;
                }

                _coupling = DenseMatrix.OfArray(couplingArray);
                _couplingTranspose = _coupling.Transpose();
                _diag = DenseMatrix.OfArray(diagArray);
            }

            public double[,] BuildCostMatrix(IReadOnlyList<int> indices)
            {
                int size = indices.Count;
                var result = GetScratchMatrix(size);
                if (size == 0)
                    return result;

                if (_electrodeCount <= 1)
                {
                    Array.Clear(result, 0, result.Length);
                    return result;
                }

                AssembleStiffnessMatrix();

                var stiffness = DenseMatrix.OfArray(_workingStiffness);
                var factor = stiffness.Cholesky();
                var kInvBt = factor.Solve(_couplingTranspose);
                var schur = _diag - _coupling * kInvBt;
                SymmetrizeInPlace((DenseMatrix)schur);

                var reduced = RemoveGauge(schur, _gaugeIndex);
                for (int i = 0; i < reduced.RowCount; i++)
                    reduced[i, i] += 1e-12;

                var reducedFactorization = reduced.Cholesky();
                var reducedInverse = reducedFactorization.Solve(DenseMatrix.CreateIdentity(reduced.RowCount));

                for (int i = 0; i < size; i++)
                {
                    result[i, i] = 0.0;
                    for (int j = i + 1; j < size; j++)
                    {
                        double groundCost = GroundCost(reducedInverse, indices[i], indices[j]);
                        result[i, j] = groundCost;
                        result[j, i] = groundCost;
                    }
                }

                return result;
            }

            private void AssembleStiffnessMatrix()
            {
                Array.Copy(_fixedStiffness, _workingStiffness, _fixedStiffness.Length);

                foreach (var contribution in _elementContributions)
                {
                    double sigma = contribution.Element.Conductivity;
                    if (!double.IsFinite(sigma))
                        sigma = 0.0;

                    int[] nodes = contribution.NodeIndices;
                    double[] local = contribution.LocalStiffness;
                    for (int a = 0; a < 3; a++)
                    {
                        int row = nodes[a];
                        int offset = a * 3;
                        for (int b = 0; b < 3; b++)
                            _workingStiffness[row, nodes[b]] += sigma * local[offset + b];
                    }
                }
            }

            private double GroundCost(Matrix<double> reducedInverse, int a, int b)
            {
                if (_electrodeCount == 0 || a == b)
                    return 0.0;

                int reducedA = _fullToReducedIndex[a];
                int reducedB = _fullToReducedIndex[b];

                if (reducedA < 0)
                    return Math.Max(0.0, reducedInverse[reducedB, reducedB]);

                if (reducedB < 0)
                    return Math.Max(0.0, reducedInverse[reducedA, reducedA]);

                // For rhs = e_a - e_b, the Dirichlet energy is rhs^T S^{-1} rhs.
                double diagonal = reducedInverse[reducedA, reducedA] + reducedInverse[reducedB, reducedB];
                double offDiagonal = 2.0 * reducedInverse[reducedA, reducedB];
                return Math.Max(0.0, diagonal - offDiagonal);
            }

            private double[,] GetScratchMatrix(int size)
            {
                if (!_costScratch.TryGetValue(size, out var matrix))
                {
                    matrix = new double[size, size];
                    _costScratch[size] = matrix;
                }

                return matrix;
            }

            private static int[] BuildReducedIndexLookup(int electrodeCount, int gaugeIndex)
            {
                var lookup = new int[electrodeCount];
                int next = 0;
                for (int i = 0; i < electrodeCount; i++)
                {
                    if (i == gaugeIndex)
                    {
                        lookup[i] = -1;
                        continue;
                    }

                    lookup[i] = next++;
                }

                return lookup;
            }

            private static DenseMatrix RemoveGauge(Matrix<double> matrix, int gauge)
            {
                int n = matrix.RowCount;
                var reduced = new double[n - 1, n - 1];
                int row = 0;
                for (int i = 0; i < n; i++)
                {
                    if (i == gauge)
                        continue;

                    int col = 0;
                    for (int j = 0; j < n; j++)
                    {
                        if (j == gauge)
                            continue;

                        reduced[row, col++] = matrix[i, j];
                    }

                    row++;
                }

                return DenseMatrix.OfArray(reduced);
            }

            private static void SymmetrizeInPlace(DenseMatrix matrix)
            {
                int n = matrix.RowCount;
                for (int i = 0; i < n; i++)
                {
                    for (int j = i + 1; j < n; j++)
                    {
                        double avg = 0.5 * (matrix[i, j] + matrix[j, i]);
                        matrix[i, j] = avg;
                        matrix[j, i] = avg;
                    }
                }
            }

            private sealed class ElementContribution
            {
                public required FEMElement Element { get; init; }
                public required int[] NodeIndices { get; init; }
                public required double[] LocalStiffness { get; init; }

                public static ElementContribution Create(FEMElement element, IReadOnlyDictionary<int, int> vertexIndexLookup)
                {
                    var nodes = new int[element.Vertices.Count];
                    for (int i = 0; i < element.Vertices.Count; i++)
                        nodes[i] = vertexIndexLookup[element.Vertices[i].GlobalId];

                    var local = new double[element.Vertices.Count * element.Vertices.Count];
                    for (int a = 0; a < element.Vertices.Count; a++)
                    {
                        for (int b = 0; b < element.Vertices.Count; b++)
                            local[a * element.Vertices.Count + b] = element.Area * element.DotProducts[a][b];
                    }

                    return new ElementContribution
                    {
                        Element = element,
                        NodeIndices = nodes,
                        LocalStiffness = local
                    };
                }
            }
        }
    }
}
