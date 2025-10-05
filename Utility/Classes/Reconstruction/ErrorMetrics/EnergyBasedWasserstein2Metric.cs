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
    /// </summary>
    public sealed class EnergyBasedWasserstein2Metric : IErrorMetric
    {
        private const double Tiny = 1e-12;
        private CachedResult? _last;

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
                if (double.IsNaN(measured[i]))
                    continue;
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

            var energy = new ElectrodeEnergyOperator(fem);
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

            return new CachedResult(measured, simulated, loss, adjointFull, include.ToArray(), sourcePotential, sourceGradient, transport.Plan);
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

        private static OptimalTransportSolution SolveOptimalTransport(double[,] cost, double[] source, double[] target)
        {
            int m = source.Length;
            int n = target.Length;

            var solver = Solver.CreateSolver("GLOP")
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
                    objective.SetCoefficient(plan[i, j], cost[i, j]);
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
                alpha[i] = row[i].DualValue();
            for (int j = 0; j < n; j++)
                beta[j] = col[j].DualValue();

            return new OptimalTransportSolution(planMatrix, alpha, beta, objectiveValue);
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
                return new CachedResult(measured, simulated, 0.0, zeros, Array.Empty<int>(), Array.Empty<double>(), Array.Empty<double>(), new double[0, 0]);
            }
        }

        private sealed record OptimalTransportSolution(double[,] Plan, double[] Alpha, double[] Beta, double Objective);

        private sealed class ElectrodeEnergyOperator
        {
            private const double ContactImpedanceFloor = 1e-12;

            private readonly DenseMatrix _schur;
            private readonly int _electrodeCount;
            private readonly int _gaugeIndex;
            private readonly Cholesky<double>? _reducedFactorization;

            public ElectrodeEnergyOperator(FEMMesh mesh)
            {
                var electrodes = mesh.GetElectrodes().Cast<FEMElectrode>().ToList();
                _electrodeCount = electrodes.Count;
                _gaugeIndex = Math.Max(0, _electrodeCount - 1);

                if (_electrodeCount == 0)
                {
                    _schur = DenseMatrix.Create(0, 0, 0.0);
                    _reducedFactorization = null;
                    return;
                }

                int nodeCount = mesh.Vertices.Count;
                double[,] stiffnessArray = new double[nodeCount, nodeCount];

                foreach (var element in mesh.ElementsTyped.Cast<FEMElement>())
                {
                    double sigma = element.Conductivity;
                    double area = element.Area;
                    for (int a = 0; a < element.Vertices.Count; a++)
                    {
                        int i = element.Vertices[a].GlobalId;
                        for (int b = 0; b < element.Vertices.Count; b++)
                        {
                            int j = element.Vertices[b].GlobalId;
                            double dot = element.DotProducts[a][b];
                            stiffnessArray[i, j] += sigma * area * dot;
                        }
                    }
                }

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
                        foreach (int vid in electrode.FEMVertexIds)
                        {
                            stiffnessArray[vid, vid] += invZ * h;
                            couplingArray[ell, vid] += invZ * h;
                        }
                    }
                    else
                    {
                        int vid = electrode.MeshId;
                        stiffnessArray[vid, vid] += invZ * h;
                        couplingArray[ell, vid] += invZ * h;
                    }

                    diagArray[ell, ell] = length * invZ;
                }

                var stiffness = DenseMatrix.OfArray(stiffnessArray);
                var coupling = DenseMatrix.OfArray(couplingArray);
                var diag = DenseMatrix.OfArray(diagArray);

                var factor = stiffness.Cholesky();
                var kInvBt = factor.Solve(coupling.Transpose());
                var schur = diag - coupling * kInvBt;
                SymmetrizeInPlace(schur);
                _schur = schur;

                if (_electrodeCount <= 1)
                {
                    _reducedFactorization = null;
                    return;
                }

                var reduced = RemoveGauge(_schur, _gaugeIndex);
                for (int i = 0; i < reduced.RowCount; i++)
                    reduced[i, i] += 1e-12;
                _reducedFactorization = reduced.Cholesky();
            }

            public double[,] BuildCostMatrix(IReadOnlyList<int> indices)
            {
                int m = indices.Count;
                var result = new double[m, m];
                if (m == 0)
                    return result;

                for (int i = 0; i < m; i++)
                {
                    result[i, i] = 0.0;
                    for (int j = i + 1; j < m; j++)
                    {
                        double cost = GroundCost(indices[i], indices[j]);
                        result[i, j] = cost;
                        result[j, i] = cost;
                    }
                }
                return result;
            }

            private double GroundCost(int a, int b)
            {
                if (_electrodeCount == 0 || a == b)
                    return 0.0;

                if (_electrodeCount == 1)
                    return 0.0;

                var rhs = new double[_electrodeCount - 1];
                int idx = 0;
                for (int k = 0; k < _electrodeCount; k++)
                {
                    if (k == _gaugeIndex)
                        continue;

                    double value = 0.0;
                    if (k == a) value += 1.0;
                    if (k == b) value -= 1.0;
                    rhs[idx++] = value;
                }

                var rhsVec = DenseVector.OfArray(rhs);
                var sol = _reducedFactorization!.Solve(rhsVec);
                return rhsVec.DotProduct(sol);
            }

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
