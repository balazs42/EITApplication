using System.Collections.Generic;
using System.Linq;
using MathNet.Numerics.LinearAlgebra.Double;
using Utility.Classes.Discretizer.FiniteElementMesh;
using Utility.Classes.Measurement;
using Utility.Classes.ReconstructionParameters;
using Utility.Classes.Solvers;
using Utility.Classes.Solvers.FiniteElementSolver;

using Matrix = MathNet.Numerics.LinearAlgebra.Matrix<double>;

namespace Utility.Classes.Reconstruction.Convexification
{
    /// <summary>
    /// Per-frame diagnostics of the practical convexification objective. The
    /// current FEM basis is only a surrogate of the chapter's H2 setting, so
    /// these values are meant for stable least-squares descent rather than
    /// exact functional analysis.
    /// </summary>
    public sealed class ConvexificationFrameObjectiveSnapshot
    {
        public required Dictionary<int, double> L1 { get; init; }
        public required Dictionary<int, double> L2 { get; init; }
        public required double[] DirichletMismatchR { get; init; }
        public required double[] DirichletMismatchS { get; init; }
        public required double[] NeumannMismatchR { get; init; }
        public required double[] NeumannMismatchS { get; init; }
        public double InteriorValue { get; init; }
        public double BoundaryValue { get; init; }
        public double RegularizationValue { get; init; }
        public double TotalValue => InteriorValue + BoundaryValue + RegularizationValue;
    }

    /// <summary>
    /// Aggregated diagnostics of the Carleman-weighted convexification objective.
    /// The persistence layer uses this to drive the inner least-squares descent
    /// and to expose objective trends for validation.
    /// </summary>
    public sealed class ConvexificationObjectiveSnapshot
    {
        public required IReadOnlyList<ConvexificationFrameObjectiveSnapshot> Frames { get; init; }
        public double InteriorValue { get; init; }
        public double BoundaryValue { get; init; }
        public double RegularizationValue { get; init; }
        public double TotalValue => InteriorValue + BoundaryValue + RegularizationValue;
    }

    /// <summary>
    /// Discrete helper operators shared by the convexification persistence and
    /// the associated self-tests. The methods below are intentionally isolated
    /// because the current FEM basis is only a practical surrogate of the
    /// chapter's ideal H2/C1 discretization.
    /// </summary>
    public static class ConvexificationOperators
    {
        private const double SmallDenominator = 1e-12;
        private const double MaximumExponent = 50.0;

        /// <summary>
        /// Normalises the supplied Carleman direction and falls back to the x-axis
        /// when the input is missing or degenerate.
        /// </summary>
        public static double[] NormalizeOmega(double[]? omega)
        {
            if (omega == null || omega.Length < 2)
                return new[] { 1.0, 0.0 };

            double x = double.IsFinite(omega[0]) ? omega[0] : 0.0;
            double y = double.IsFinite(omega[1]) ? omega[1] : 0.0;
            double norm = Math.Sqrt(x * x + y * y);
            if (norm < SmallDenominator)
                return new[] { 1.0, 0.0 };

            return new[] { x / norm, y / norm };
        }

        /// <summary>
        /// Computes the positivity shift c(t) used by the raw finite-electrode
        /// proxy g0 = U - z I / |E| + c(t). The helper is intentionally tiny so
        /// the log-domain safety rule can be validated independently.
        /// </summary>
        public static double ComputePositivityShift(double rawMin, double d0, double positivityMargin)
        {
            double safeRawMin = double.IsFinite(rawMin) ? rawMin : 0.0;
            double safeD0 = double.IsFinite(d0) ? d0 : 0.0;
            double safeMargin = double.IsFinite(positivityMargin) ? Math.Max(0.0, positivityMargin) : 0.0;
            return Math.Max(0.0, safeD0 - safeRawMin) + safeMargin;
        }

        /// <summary>
        /// Computes frame-wise drive derivatives for electrode-wise signals. The
        /// helper sorts by the supplied step indices, supports periodic wrap-around
        /// when a full cycle is available, and restores the original frame order in
        /// the returned buffers.
        /// </summary>
        public static List<double[]> ComputeDriveDerivatives(IReadOnlyList<double[]> samples,
                                                             IReadOnlyList<int> stepIndices,
                                                             int cycleLength,
                                                             bool usePeriodicWhenAvailable)
            => ComputeDriveDerivatives(samples,
                                       stepIndices,
                                       cycleLength,
                                       usePeriodicWhenAvailable,
                                       smoothingWindow: 0,
                                       smoothingPasses: 0,
                                       usePeriodicSmoothing: true);

        /// <summary>
        /// Computes frame-wise drive derivatives for electrode-wise signals, with
        /// optional pre-smoothing before the finite-difference stage.
        /// </summary>
        public static List<double[]> ComputeDriveDerivatives(IReadOnlyList<double[]> samples,
                                                             IReadOnlyList<int> stepIndices,
                                                             int cycleLength,
                                                             bool usePeriodicWhenAvailable,
                                                             int smoothingWindow,
                                                             int smoothingPasses,
                                                             bool usePeriodicSmoothing)
        {
            if (samples == null)
                throw new ArgumentNullException(nameof(samples));
            if (stepIndices == null)
                throw new ArgumentNullException(nameof(stepIndices));
            if (samples.Count != stepIndices.Count)
                throw new ArgumentException("Sample count and step-index count must agree.");
            if (samples.Count == 0)
                return [];

            int signalLength = samples[0].Length;
            foreach (var frame in samples)
            {
                if (frame.Length != signalLength)
                    throw new ArgumentException("All sample frames must have the same electrode count.");
            }

            var ordered = samples
                .Select((frame, index) => new OrderedFrame(index, NormalizeStep(stepIndices[index], cycleLength), frame))
                .ToList();

            bool distinctOrdering = ordered.Select(item => item.StepIndex).Distinct().Count() == ordered.Count;
            if (distinctOrdering)
                ordered.Sort((left, right) => left.StepIndex.CompareTo(right.StepIndex));

            bool usePeriodic = usePeriodicWhenAvailable
                               && cycleLength > 1
                               && ordered.Count == cycleLength
                               && distinctOrdering;
            bool usePeriodicSmoothingMode = usePeriodicSmoothing
                                            && cycleLength > 1
                                            && ordered.Count == cycleLength
                                            && distinctOrdering;

            var smoothedSamples = SmoothOrderedSamples(ordered,
                                                       signalLength,
                                                       smoothingWindow,
                                                       smoothingPasses,
                                                       usePeriodicSmoothingMode);

            var derivativeBySortedIndex = new double[ordered.Count][];
            for (int i = 0; i < ordered.Count; i++)
                derivativeBySortedIndex[i] = new double[signalLength];

            for (int i = 0; i < ordered.Count; i++)
            {
                for (int electrode = 0; electrode < signalLength; electrode++)
                {
                    derivativeBySortedIndex[i][electrode] = DifferentiateAt(ordered,
                                                                            smoothedSamples,
                                                                            i,
                                                                            electrode,
                                                                            cycleLength,
                                                                            usePeriodic);
                }
            }

            var restored = Enumerable.Range(0, ordered.Count)
                .Select(_ => new double[signalLength])
                .ToList();

            for (int sortedIndex = 0; sortedIndex < ordered.Count; sortedIndex++)
            {
                int originalIndex = ordered[sortedIndex].OriginalIndex;
                restored[originalIndex] = derivativeBySortedIndex[sortedIndex];
            }

            return restored;
        }

        /// <summary>
        /// Applies optional electrode-wise smoothing along the ordered drive cycle.
        /// The smoothing is disabled when the window is 0 or 1, matching the
        /// "off by default" behavior requested by the task.
        /// </summary>
        public static List<double[]> SmoothDriveSamples(IReadOnlyList<double[]> samples,
                                                        IReadOnlyList<int> stepIndices,
                                                        int cycleLength,
                                                        int smoothingWindow,
                                                        int smoothingPasses,
                                                        bool usePeriodicSmoothing)
        {
            if (samples == null)
                throw new ArgumentNullException(nameof(samples));
            if (stepIndices == null)
                throw new ArgumentNullException(nameof(stepIndices));
            if (samples.Count != stepIndices.Count)
                throw new ArgumentException("Sample count and step-index count must agree.");
            if (samples.Count == 0)
                return [];

            int signalLength = samples[0].Length;
            var ordered = samples
                .Select((frame, index) => new OrderedFrame(index, NormalizeStep(stepIndices[index], cycleLength), frame))
                .ToList();

            bool distinctOrdering = ordered.Select(item => item.StepIndex).Distinct().Count() == ordered.Count;
            if (distinctOrdering)
                ordered.Sort((left, right) => left.StepIndex.CompareTo(right.StepIndex));

            bool periodic = usePeriodicSmoothing
                            && cycleLength > 1
                            && ordered.Count == cycleLength
                            && distinctOrdering;
            var smoothed = SmoothOrderedSamples(ordered,
                                                signalLength,
                                                smoothingWindow,
                                                smoothingPasses,
                                                periodic);

            var restored = Enumerable.Range(0, ordered.Count)
                .Select(_ => new double[signalLength])
                .ToList();

            for (int sortedIndex = 0; sortedIndex < ordered.Count; sortedIndex++)
            {
                int originalIndex = ordered[sortedIndex].OriginalIndex;
                restored[originalIndex] = smoothed[sortedIndex];
            }

            return restored;
        }

        /// <summary>
        /// Reconstructs electrode potentials from measured potential differences by
        /// solving a small zero-mean least-squares problem on the electrode graph.
        /// </summary>
        public static double[] ReconstructPotentialsFromDifferences(int electrodeCount,
                                                                    IReadOnlyList<ElectrodePair> measurementPairs,
                                                                    IReadOnlyList<double> differences)
        {
            if (electrodeCount <= 0)
                return [];
            if (measurementPairs == null)
                throw new ArgumentNullException(nameof(measurementPairs));
            if (differences == null)
                throw new ArgumentNullException(nameof(differences));
            if (measurementPairs.Count != differences.Count)
                throw new ArgumentException("Measurement-pair count and difference count must agree.");

            if (measurementPairs.Count == 0)
                return new double[electrodeCount];

            int rowCount = measurementPairs.Count + 1;
            var system = DenseMatrix.Create(rowCount, electrodeCount, 0.0);
            var rhs = DenseVector.Create(rowCount, 0.0);

            for (int row = 0; row < measurementPairs.Count; row++)
            {
                var pair = measurementPairs[row];
                if (pair.First >= 0 && pair.First < electrodeCount)
                    system[row, pair.First] = 1.0;
                if (pair.Second >= 0 && pair.Second < electrodeCount)
                    system[row, pair.Second] = -1.0;
                rhs[row] = double.IsFinite(differences[row]) ? differences[row] : 0.0;
            }

            double gaugeScale = 1.0 / Math.Sqrt(electrodeCount);
            for (int column = 0; column < electrodeCount; column++)
                system[rowCount - 1, column] = gaugeScale;

            var normalMatrix = system.TransposeThisAndMultiply(system);
            normalMatrix = normalMatrix + DenseMatrix.CreateIdentity(electrodeCount) * 1e-8;
            var normalRhs = system.TransposeThisAndMultiply(rhs);
            var solution = normalMatrix.Solve(normalRhs);

            var values = new double[electrodeCount];
            for (int i = 0; i < electrodeCount; i++)
                values[i] = solution[i];

            return values;
        }

        /// <summary>
        /// Fills missing or non-finite electrode amplitudes by periodic linear
        /// interpolation along the electrode cycle.
        /// </summary>
        public static double[] FillMissingElectrodeValues(IReadOnlyList<double> values)
        {
            if (values == null)
                throw new ArgumentNullException(nameof(values));

            var completed = values.Select(value => double.IsFinite(value) ? value : double.NaN).ToArray();
            if (completed.Length == 0)
                return completed;

            var known = completed
                .Select((value, index) => (value, index))
                .Where(pair => double.IsFinite(pair.value))
                .OrderBy(pair => pair.index)
                .ToList();

            if (known.Count == 0)
                return new double[completed.Length];
            if (known.Count == 1)
            {
                Array.Fill(completed, known[0].value);
                return completed;
            }

            for (int position = 0; position < known.Count; position++)
            {
                var current = known[position];
                var next = known[(position + 1) % known.Count];
                int span = ((next.index - current.index) + completed.Length) % completed.Length;
                if (span == 0)
                    span = completed.Length;

                completed[current.index] = current.value;
                for (int offset = 1; offset < span; offset++)
                {
                    int index = (current.index + offset) % completed.Length;
                    if (double.IsFinite(completed[index]))
                        continue;

                    double t = offset / (double)span;
                    completed[index] = (1.0 - t) * current.value + t * next.value;
                }
            }

            return completed;
        }

        /// <summary>
        /// Builds the cotangent-style vertex Laplacian used by the practical
        /// convexification surrogate. The returned matrix uses the same sign
        /// convention as <see cref="FiniteElementOperators.CalculateLaplacian"/>,
        /// namely off-diagonal edge couplings are positive and diagonal entries
        /// are the negative row sums.
        /// </summary>
        public static Matrix BuildCotangentLaplacianMatrix(FEMMesh mesh)
        {
            if (mesh == null)
                throw new ArgumentNullException(nameof(mesh));

            var indexById = mesh.Vertices
                .Select((vertex, index) => (vertex.GlobalId, index))
                .ToDictionary(item => item.GlobalId, item => item.index);

            var matrix = SparseMatrix.Create(mesh.Vertices.Count, mesh.Vertices.Count, 0.0);
            var edgeWeights = BuildCotangentWeights(mesh);

            foreach (var pair in edgeWeights)
            {
                int firstId = (int)(pair.Key >> 32);
                int secondId = (int)(pair.Key & 0xFFFFFFFF);
                double value = 0.5 * pair.Value;

                int first = indexById[firstId];
                int second = indexById[secondId];

                matrix[first, second] += value;
                matrix[second, first] += value;
                matrix[first, first] -= value;
                matrix[second, second] -= value;
            }

            return matrix;
        }

        /// <summary>
        /// Solves a surrogate Dirichlet Poisson problem on the current FEM vertex
        /// basis using the supplied discrete Laplacian matrix.
        /// </summary>
        public static PotentialDistribution SolveDirichletPoisson(FEMMesh mesh,
                                                                  Matrix laplacian,
                                                                  IReadOnlyDictionary<int, double> boundaryValues,
                                                                  IReadOnlyDictionary<int, double>? source,
                                                                  INumericSolver numericSolver,
                                                                  double regularization = 1e-8)
        {
            if (mesh == null)
                throw new ArgumentNullException(nameof(mesh));
            if (laplacian == null)
                throw new ArgumentNullException(nameof(laplacian));
            if (boundaryValues == null)
                throw new ArgumentNullException(nameof(boundaryValues));
            if (numericSolver == null)
                throw new ArgumentNullException(nameof(numericSolver));

            int vertexCount = mesh.Vertices.Count;
            if (laplacian.RowCount != vertexCount || laplacian.ColumnCount != vertexCount)
                throw new ArgumentException("Laplacian size does not match the mesh vertex count.");

            var idToIndex = mesh.Vertices
                .Select((vertex, index) => (vertex.GlobalId, index))
                .ToDictionary(item => item.GlobalId, item => item.index);
            var indexToId = mesh.Vertices.Select(vertex => vertex.GlobalId).ToArray();

            var boundaryIndices = new HashSet<int>();
            foreach (var pair in boundaryValues)
            {
                if (idToIndex.TryGetValue(pair.Key, out int index))
                    boundaryIndices.Add(index);
            }

            var fullSolution = new double[vertexCount];
            foreach (var pair in boundaryValues)
            {
                if (idToIndex.TryGetValue(pair.Key, out int index))
                    fullSolution[index] = pair.Value;
            }

            var interiorIndices = Enumerable.Range(0, vertexCount)
                .Where(index => !boundaryIndices.Contains(index))
                .ToArray();

            if (interiorIndices.Length == 0)
                return ToPotentialDistribution(mesh, fullSolution);

            var boundaryIndexList = boundaryIndices.OrderBy(index => index).ToArray();
            var system = DenseMatrix.Create(interiorIndices.Length, interiorIndices.Length, 0.0);
            var rhs = DenseVector.Create(interiorIndices.Length, 0.0);

            for (int row = 0; row < interiorIndices.Length; row++)
            {
                int globalRow = interiorIndices[row];
                int vertexId = indexToId[globalRow];
                rhs[row] = source != null && source.TryGetValue(vertexId, out double sourceValue)
                    ? sourceValue
                    : 0.0;

                for (int column = 0; column < interiorIndices.Length; column++)
                    system[row, column] = laplacian[globalRow, interiorIndices[column]];

                for (int column = 0; column < boundaryIndexList.Length; column++)
                {
                    int boundaryIndex = boundaryIndexList[column];
                    rhs[row] -= laplacian[globalRow, boundaryIndex] * fullSolution[boundaryIndex];
                }

                system[row, row] -= regularization;
            }

            var solution = numericSolver.SolveLinearSystem(system, rhs);
            for (int row = 0; row < interiorIndices.Length; row++)
                fullSolution[interiorIndices[row]] = solution[row];

            return ToPotentialDistribution(mesh, fullSolution);
        }

        /// <summary>
        /// Computes the Carleman weights psi_lambda at element centroids.
        /// </summary>
        public static Dictionary<int, double> ComputeCarlemanWeights(FEMMesh mesh, double lambda, double[] omega)
        {
            if (mesh == null)
                throw new ArgumentNullException(nameof(mesh));

            var normalizedOmega = NormalizeOmega(omega);
            var weights = new Dictionary<int, double>(mesh.ElementsTyped.Count);
            foreach (var element in mesh.ElementsTyped.Cast<FEMElement>())
            {
                double cx = element.Vertices.Average(vertex => vertex.X);
                double cy = element.Vertices.Average(vertex => vertex.Y);
                double phase = 2.0 * lambda * (normalizedOmega[0] * cx + normalizedOmega[1] * cy);
                weights[element.Id] = Math.Exp(Math.Clamp(phase, -MaximumExponent, MaximumExponent));
            }

            return weights;
        }

        /// <summary>
        /// Builds a dense boundary-value map by assigning exact electrode values on
        /// contact vertices and linearly interpolating across the remaining
        /// boundary nodes.
        /// </summary>
        public static Dictionary<int, double> CreateBoundaryValueMap(FEMMesh mesh,
                                                                     IReadOnlyList<FEMElectrode> electrodes,
                                                                     IReadOnlyList<double> electrodeValues)
        {
            if (mesh == null)
                throw new ArgumentNullException(nameof(mesh));
            if (electrodes == null)
                throw new ArgumentNullException(nameof(electrodes));
            if (electrodeValues == null)
                throw new ArgumentNullException(nameof(electrodeValues));
            if (electrodes.Count != electrodeValues.Count)
                throw new ArgumentException("Electrode-value count does not match the electrode count.");

            var boundary = mesh.GetOrderedBoundaryVertices();
            if (boundary.Count == 0)
                return [];

            var exactByBoundaryIndex = new Dictionary<int, double>();
            for (int electrodeIndex = 0; electrodeIndex < electrodes.Count; electrodeIndex++)
            {
                foreach (int vertexId in GetElectrodeVertexIds(electrodes[electrodeIndex]))
                {
                    if (mesh.TryGetBoundaryIndex(vertexId, out int boundaryIndex))
                        exactByBoundaryIndex[boundaryIndex] = electrodeValues[electrodeIndex];
                }
            }

            if (exactByBoundaryIndex.Count == 0)
                return boundary.ToDictionary(vertex => vertex.GlobalId, _ => 0.0);

            var sorted = exactByBoundaryIndex.Keys.OrderBy(index => index).ToList();
            var valuesByBoundaryIndex = new double[boundary.Count];
            var assigned = new bool[boundary.Count];

            foreach (var pair in exactByBoundaryIndex)
            {
                valuesByBoundaryIndex[pair.Key] = pair.Value;
                assigned[pair.Key] = true;
            }

            for (int position = 0; position < sorted.Count; position++)
            {
                int start = sorted[position];
                int end = sorted[(position + 1) % sorted.Count];
                double startValue = valuesByBoundaryIndex[start];
                double endValue = valuesByBoundaryIndex[end];

                int distance = (end - start + boundary.Count) % boundary.Count;
                if (distance == 0)
                    distance = boundary.Count;

                for (int offset = 1; offset < distance; offset++)
                {
                    int index = (start + offset) % boundary.Count;
                    if (assigned[index])
                        continue;

                    double t = offset / (double)distance;
                    valuesByBoundaryIndex[index] = (1.0 - t) * startValue + t * endValue;
                }
            }

            var map = new Dictionary<int, double>(boundary.Count);
            for (int index = 0; index < boundary.Count; index++)
                map[boundary[index].GlobalId] = valuesByBoundaryIndex[index];

            return map;
        }

        /// <summary>
        /// Evaluates the practical Carleman-weighted convexification objective on
        /// the current P1 FEM basis. This is the discrete functional the inner
        /// solver minimizes. It is not an exact Argyris/HCT realization, but it
        /// keeps the residual, Dirichlet, Neumann and stabilization terms
        /// separated so the update law can be driven by the objective itself.
        /// </summary>
        public static ConvexificationObjectiveSnapshot EvaluateObjective(FEMMesh mesh,
                                                                         IReadOnlyList<ConvexificationBoundaryData> boundaryData,
                                                                         IReadOnlyList<FEMElectrode> electrodes,
                                                                         IReadOnlyList<PotentialDistribution> rFields,
                                                                         IReadOnlyList<PotentialDistribution> sFields,
                                                                         ConvexificationOptions options,
                                                                         IReadOnlyDictionary<int, double> carlemanWeights)
        {
            if (mesh == null)
                throw new ArgumentNullException(nameof(mesh));
            if (boundaryData == null)
                throw new ArgumentNullException(nameof(boundaryData));
            if (electrodes == null)
                throw new ArgumentNullException(nameof(electrodes));
            if (rFields == null)
                throw new ArgumentNullException(nameof(rFields));
            if (sFields == null)
                throw new ArgumentNullException(nameof(sFields));
            if (options == null)
                throw new ArgumentNullException(nameof(options));
            if (carlemanWeights == null)
                throw new ArgumentNullException(nameof(carlemanWeights));
            if (boundaryData.Count != rFields.Count || boundaryData.Count != sFields.Count)
                throw new ArgumentException("Boundary-data count and field counts must agree.");

            double h = Math.Max(options.ElectrodeLengthFloor,
                                electrodes.Count > 0
                                    ? electrodes.Average(electrode => ResolveElectrodeLength(electrode, options.ElectrodeLengthFloor))
                                    : options.ElectrodeLengthFloor);
            double dirichletScale = options.BoundaryDirichletWeight / Math.Pow(h, 3);
            double neumannScale = options.BoundaryNeumannWeight / h;

            double totalInterior = 0.0;
            double totalBoundary = 0.0;
            double totalRegularization = 0.0;
            var frames = new List<ConvexificationFrameObjectiveSnapshot>(boundaryData.Count);

            for (int frameIndex = 0; frameIndex < boundaryData.Count; frameIndex++)
            {
                var frame = boundaryData[frameIndex];
                var residuals = ComputeResiduals(mesh, rFields[frameIndex], sFields[frameIndex], options.Epsilon);
                var gradR = FiniteElementOperators.CalculateElementWiseGradient(mesh, rFields[frameIndex]);
                var gradS = FiniteElementOperators.CalculateElementWiseGradient(mesh, sFields[frameIndex]);

                double frameInterior = 0.0;
                double frameBoundary = 0.0;
                double frameRegularization = 0.0;

                foreach (var element in mesh.ElementsTyped.Cast<FEMElement>())
                {
                    double weight = carlemanWeights.TryGetValue(element.Id, out double carleman) ? carleman : 1.0;
                    double l1 = residuals.L1.TryGetValue(element.Id, out double rValue) ? rValue : 0.0;
                    double l2 = residuals.L2.TryGetValue(element.Id, out double sValue) ? sValue : 0.0;
                    var gradRValue = gradR.GetVector(element.Id);
                    var gradSValue = gradS.GetVector(element.Id);
                    double regularization = gradRValue.X * gradRValue.X
                                            + gradRValue.Y * gradRValue.Y
                                            + gradSValue.X * gradSValue.X
                                            + gradSValue.Y * gradSValue.Y;

                    frameInterior += options.InteriorResidualWeight * weight * element.Area * (l1 * l1 + l2 * l2);
                    frameRegularization += options.Beta * weight * element.Area * regularization;
                }

                var boundaryGradR = FiniteElementOperators.CalculateElementWiseGradient(mesh, rFields[frameIndex]);
                var boundaryGradS = FiniteElementOperators.CalculateElementWiseGradient(mesh, sFields[frameIndex]);
                var dirichletMismatchR = new double[electrodes.Count];
                var dirichletMismatchS = new double[electrodes.Count];
                var neumannMismatchR = new double[electrodes.Count];
                var neumannMismatchS = new double[electrodes.Count];

                for (int electrodeIndex = 0; electrodeIndex < electrodes.Count; electrodeIndex++)
                {
                    double avgR = ComputeElectrodeAverage(mesh, electrodes[electrodeIndex], rFields[frameIndex]);
                    double avgS = ComputeElectrodeAverage(mesh, electrodes[electrodeIndex], sFields[frameIndex]);
                    double dnR = ComputeElectrodeNormalDerivative(mesh, electrodes[electrodeIndex], boundaryGradR);
                    double dnS = ComputeElectrodeNormalDerivative(mesh, electrodes[electrodeIndex], boundaryGradS);

                    dirichletMismatchR[electrodeIndex] = avgR - frame.B0[electrodeIndex];
                    dirichletMismatchS[electrodeIndex] = avgS - frame.BEpsilon[electrodeIndex];
                    neumannMismatchR[electrodeIndex] = dnR - frame.C0[electrodeIndex];
                    neumannMismatchS[electrodeIndex] = dnS - frame.CEpsilon[electrodeIndex];

                    frameBoundary += dirichletScale * (dirichletMismatchR[electrodeIndex] * dirichletMismatchR[electrodeIndex]
                                                     + dirichletMismatchS[electrodeIndex] * dirichletMismatchS[electrodeIndex]);

                    if (ShouldApplyNeumannPenalty(frame, electrodeIndex, options))
                    {
                        frameBoundary += neumannScale * (neumannMismatchR[electrodeIndex] * neumannMismatchR[electrodeIndex]
                                                       + neumannMismatchS[electrodeIndex] * neumannMismatchS[electrodeIndex]);
                    }
                }

                totalInterior += frameInterior;
                totalBoundary += frameBoundary;
                totalRegularization += frameRegularization;

                frames.Add(new ConvexificationFrameObjectiveSnapshot
                {
                    L1 = residuals.L1,
                    L2 = residuals.L2,
                    DirichletMismatchR = dirichletMismatchR,
                    DirichletMismatchS = dirichletMismatchS,
                    NeumannMismatchR = neumannMismatchR,
                    NeumannMismatchS = neumannMismatchS,
                    InteriorValue = frameInterior,
                    BoundaryValue = frameBoundary,
                    RegularizationValue = frameRegularization
                });
            }

            return new ConvexificationObjectiveSnapshot
            {
                Frames = frames,
                InteriorValue = totalInterior,
                BoundaryValue = totalBoundary,
                RegularizationValue = totalRegularization
            };
        }

        /// <summary>
        /// Builds preconditioned descent directions for the practical
        /// convexification objective. The directions are obtained by
        /// back-projecting the weighted residuals and boundary mismatches and
        /// solving a Poisson-type Riesz map on the current P1 space. This is a
        /// Gauss-Newton-like surrogate tailored to the existing FEM basis.
        /// </summary>
        public static (List<PotentialDistribution> RDirections,
                       List<PotentialDistribution> SDirections,
                       double DirectionNorm) BuildPreconditionedDescentDirections(
            FEMMesh mesh,
            IReadOnlyList<ConvexificationBoundaryData> boundaryData,
            IReadOnlyList<FEMElectrode> electrodes,
            IReadOnlyList<PotentialDistribution> currentR,
            IReadOnlyList<PotentialDistribution> currentS,
            ConvexificationObjectiveSnapshot objective,
            IReadOnlyDictionary<int, double> carlemanWeights,
            Matrix laplacian,
            INumericSolver numericSolver,
            ConvexificationOptions options)
        {
            if (mesh == null)
                throw new ArgumentNullException(nameof(mesh));
            if (boundaryData == null)
                throw new ArgumentNullException(nameof(boundaryData));
            if (electrodes == null)
                throw new ArgumentNullException(nameof(electrodes));
            if (currentR == null)
                throw new ArgumentNullException(nameof(currentR));
            if (currentS == null)
                throw new ArgumentNullException(nameof(currentS));
            if (objective == null)
                throw new ArgumentNullException(nameof(objective));
            if (carlemanWeights == null)
                throw new ArgumentNullException(nameof(carlemanWeights));
            if (laplacian == null)
                throw new ArgumentNullException(nameof(laplacian));
            if (numericSolver == null)
                throw new ArgumentNullException(nameof(numericSolver));
            if (options == null)
                throw new ArgumentNullException(nameof(options));

            double h = Math.Max(options.ElectrodeLengthFloor,
                                electrodes.Count > 0
                                    ? electrodes.Average(electrode => ResolveElectrodeLength(electrode, options.ElectrodeLengthFloor))
                                    : options.ElectrodeLengthFloor);

            var rDirections = new List<PotentialDistribution>(currentR.Count);
            var sDirections = new List<PotentialDistribution>(currentS.Count);
            double directionNorm = 0.0;

            for (int frameIndex = 0; frameIndex < currentR.Count; frameIndex++)
            {
                var frameObjective = objective.Frames[frameIndex];

                var elementSourceR = new Dictionary<int, double>(mesh.ElementsTyped.Count);
                var elementSourceS = new Dictionary<int, double>(mesh.ElementsTyped.Count);
                foreach (var element in mesh.ElementsTyped.Cast<FEMElement>())
                {
                    double weight = carlemanWeights.TryGetValue(element.Id, out double carleman) ? carleman : 1.0;
                    double l1 = frameObjective.L1.TryGetValue(element.Id, out double rValue) ? rValue : 0.0;
                    double l2 = frameObjective.L2.TryGetValue(element.Id, out double sValue) ? sValue : 0.0;

                    // Practical Gauss-Newton surrogate:
                    // r influences both L1 and L2 more directly, so its descent
                    // source uses 2*L1 + L2. The s update uses the symmetric
                    // 2*L2 + L1 combination.
                    elementSourceR[element.Id] = options.InteriorResidualWeight * weight * (2.0 * l1 + l2);
                    elementSourceS[element.Id] = options.InteriorResidualWeight * weight * (l1 + 2.0 * l2);
                }

                var vertexSourceR = ProjectElementFieldToVertices(mesh, elementSourceR);
                var vertexSourceS = ProjectElementFieldToVertices(mesh, elementSourceS);
                var lapR = FiniteElementOperators.CalculateLaplacian(mesh, currentR[frameIndex]);
                var lapS = FiniteElementOperators.CalculateLaplacian(mesh, currentS[frameIndex]);

                foreach (var vertex in mesh.Vertices)
                {
                    int vertexId = vertex.GlobalId;
                    vertexSourceR.SetValue(vertexId, vertexSourceR.GetPotential(vertexId) - options.Beta * lapR.GetPotential(vertexId));
                    vertexSourceS.SetValue(vertexId, vertexSourceS.GetPotential(vertexId) - options.Beta * lapS.GetPotential(vertexId));
                }

                double[] boundaryDescentR = BuildBoundaryDescentValues(boundaryData[frameIndex],
                                                                        frameObjective.DirichletMismatchR,
                                                                        frameObjective.NeumannMismatchR,
                                                                        electrodes,
                                                                        h,
                                                                        options);
                double[] boundaryDescentS = BuildBoundaryDescentValues(boundaryData[frameIndex],
                                                                        frameObjective.DirichletMismatchS,
                                                                        frameObjective.NeumannMismatchS,
                                                                        electrodes,
                                                                        h,
                                                                        options);

                var directionR = SolveDirichletPoisson(mesh,
                                                       laplacian,
                                                       CreateBoundaryValueMap(mesh, electrodes, boundaryDescentR),
                                                       vertexSourceR.Potentials,
                                                       numericSolver,
                                                       options.SigmaRecoveryRegularization);
                var directionS = SolveDirichletPoisson(mesh,
                                                       laplacian,
                                                       CreateBoundaryValueMap(mesh, electrodes, boundaryDescentS),
                                                       vertexSourceS.Potentials,
                                                       numericSolver,
                                                       options.SigmaRecoveryRegularization);

                rDirections.Add(directionR);
                sDirections.Add(directionS);
                directionNorm += ComputeFieldNorm(directionR);
                directionNorm += ComputeFieldNorm(directionS);
            }

            return (rDirections, sDirections, directionNorm);
        }

        /// <summary>
        /// Computes the average of a nodal scalar field on one electrode patch.
        /// </summary>
        public static double ComputeElectrodeAverage(FEMMesh mesh, FEMElectrode electrode, ScalarField field)
        {
            if (mesh == null)
                throw new ArgumentNullException(nameof(mesh));
            if (electrode == null)
                throw new ArgumentNullException(nameof(electrode));
            if (field == null)
                throw new ArgumentNullException(nameof(field));

            var ids = GetElectrodeVertexIds(electrode).ToList();
            if (ids.Count == 0)
                return 0.0;

            double sum = 0.0;
            foreach (int id in ids)
                sum += field.GetValue(id);

            return sum / ids.Count;
        }

        /// <summary>
        /// Computes a practical electrode-average normal derivative by projecting
        /// element-wise gradients onto an outward direction estimated from the
        /// electrode centroid and the boundary centroid.
        /// </summary>
        public static double ComputeElectrodeNormalDerivative(FEMMesh mesh,
                                                              FEMElectrode electrode,
                                                              VectorField elementGradients)
        {
            if (mesh == null)
                throw new ArgumentNullException(nameof(mesh));
            if (electrode == null)
                throw new ArgumentNullException(nameof(electrode));
            if (elementGradients == null)
                throw new ArgumentNullException(nameof(elementGradients));

            var boundary = mesh.GetOrderedBoundaryVertices();
            var contactIds = GetElectrodeVertexIds(electrode).ToHashSet();
            if (contactIds.Count == 0 || boundary.Count == 0)
                return 0.0;

            if (!TryGetElectrodeOutwardNormal(mesh, contactIds, out double nx, out double ny))
                return 0.0;

            var touchingElements = mesh.ElementsTyped
                .Cast<FEMElement>()
                .Where(element => element.Vertices.Any(vertex => contactIds.Contains(vertex.GlobalId)))
                .ToList();

            if (touchingElements.Count == 0)
                return 0.0;

            double total = 0.0;
            foreach (var element in touchingElements)
            {
                var gradient = elementGradients.GetVector(element.Id);
                total += gradient.X * nx + gradient.Y * ny;
            }

            return total / touchingElements.Count;
        }

        /// <summary>
        /// Builds a linear operator that maps nodal V values to electrode-average
        /// normal derivatives. This is used in the quasi-reversibility V-stage so
        /// the practical Neumann condition d_n V = 0 enters the least-squares
        /// system directly rather than as a post-hoc tweak.
        /// </summary>
        public static Matrix BuildElectrodeNormalDerivativeMatrix(FEMMesh mesh,
                                                                  IReadOnlyList<FEMElectrode> electrodes)
        {
            if (mesh == null)
                throw new ArgumentNullException(nameof(mesh));
            if (electrodes == null)
                throw new ArgumentNullException(nameof(electrodes));

            int vertexCount = mesh.Vertices.Count;
            var idToIndex = mesh.Vertices
                .Select((vertex, index) => (vertex.GlobalId, index))
                .ToDictionary(item => item.GlobalId, item => item.index);
            var matrix = DenseMatrix.Create(electrodes.Count, vertexCount, 0.0);

            for (int electrodeIndex = 0; electrodeIndex < electrodes.Count; electrodeIndex++)
            {
                var contactIds = GetElectrodeVertexIds(electrodes[electrodeIndex]).ToHashSet();
                if (contactIds.Count == 0)
                    continue;
                if (!TryGetElectrodeOutwardNormal(mesh, contactIds, out double nx, out double ny))
                    continue;

                var touchingElements = mesh.ElementsTyped
                    .Cast<FEMElement>()
                    .Where(element => element.Vertices.Any(vertex => contactIds.Contains(vertex.GlobalId)))
                    .ToList();

                if (touchingElements.Count == 0)
                    continue;

                double scale = 1.0 / touchingElements.Count;
                foreach (var element in touchingElements)
                {
                    double twoA = 2.0 * Math.Max(element.Area, SmallDenominator);
                    var v1 = element.Vertices[0];
                    var v2 = element.Vertices[1];
                    var v3 = element.Vertices[2];

                    var gradients = new (FEMVertex Vertex, double Gx, double Gy)[]
                    {
                        (v1, (v2.Y - v3.Y) / twoA, (v3.X - v2.X) / twoA),
                        (v2, (v3.Y - v1.Y) / twoA, (v1.X - v3.X) / twoA),
                        (v3, (v1.Y - v2.Y) / twoA, (v2.X - v1.X) / twoA)
                    };

                    foreach (var basis in gradients)
                    {
                        if (!idToIndex.TryGetValue(basis.Vertex.GlobalId, out int column))
                            continue;

                        matrix[electrodeIndex, column] += scale * (basis.Gx * nx + basis.Gy * ny);
                    }
                }
            }

            return matrix;
        }

        /// <summary>
        /// Projects element-wise scalar data back to the mesh vertices using
        /// simple averaging over the adjacent elements.
        /// </summary>
        public static PotentialDistribution ProjectElementFieldToVertices(FEMMesh mesh,
                                                                          IReadOnlyDictionary<int, double> elementValues)
        {
            if (mesh == null)
                throw new ArgumentNullException(nameof(mesh));
            if (elementValues == null)
                throw new ArgumentNullException(nameof(elementValues));

            var sums = mesh.Vertices.ToDictionary(vertex => vertex.GlobalId, _ => 0.0);
            var counts = mesh.Vertices.ToDictionary(vertex => vertex.GlobalId, _ => 0);

            foreach (var element in mesh.ElementsTyped.Cast<FEMElement>())
            {
                double value = elementValues.TryGetValue(element.Id, out double elementValue) ? elementValue : 0.0;
                foreach (var vertex in element.Vertices)
                {
                    sums[vertex.GlobalId] += value;
                    counts[vertex.GlobalId]++;
                }
            }

            var projected = new Dictionary<int, double>(sums.Count);
            foreach (var vertex in mesh.Vertices)
            {
                int count = counts[vertex.GlobalId];
                projected[vertex.GlobalId] = count > 0 ? sums[vertex.GlobalId] / count : 0.0;
            }

            return new PotentialDistribution(projected);
        }

        /// <summary>
        /// Builds the nonlinear source term -2 grad(r).grad(w) on elements.
        /// </summary>
        public static Dictionary<int, double> ComputeNonlinearSource(FEMMesh mesh,
                                                                     PotentialDistribution rField,
                                                                     PotentialDistribution sField,
                                                                     double epsilon)
        {
            if (mesh == null)
                throw new ArgumentNullException(nameof(mesh));
            if (rField == null)
                throw new ArgumentNullException(nameof(rField));
            if (sField == null)
                throw new ArgumentNullException(nameof(sField));

            var wField = BuildWField(rField, sField, epsilon);
            var gradR = FiniteElementOperators.CalculateElementWiseGradient(mesh, rField);
            var gradW = FiniteElementOperators.CalculateElementWiseGradient(mesh, wField);
            var source = new Dictionary<int, double>(mesh.ElementsTyped.Count);

            foreach (var element in mesh.ElementsTyped.Cast<FEMElement>())
            {
                var gradRValue = gradR.GetVector(element.Id);
                var gradWValue = gradW.GetVector(element.Id);
                source[element.Id] = -2.0 * (gradRValue.X * gradWValue.X + gradRValue.Y * gradWValue.Y);
            }

            return source;
        }

        /// <summary>
        /// Computes the surrogate residuals of the closed convexification system
        /// on each element of the current mesh.
        /// </summary>
        public static (Dictionary<int, double> L1, Dictionary<int, double> L2) ComputeResiduals(FEMMesh mesh,
                                                                                                 PotentialDistribution rField,
                                                                                                 PotentialDistribution sField,
                                                                                                 double epsilon)
        {
            if (mesh == null)
                throw new ArgumentNullException(nameof(mesh));
            if (rField == null)
                throw new ArgumentNullException(nameof(rField));
            if (sField == null)
                throw new ArgumentNullException(nameof(sField));

            var wField = BuildWField(rField, sField, epsilon);
            var laplacianR = FiniteElementOperators.CalculateLaplacian(mesh, rField);
            var laplacianS = FiniteElementOperators.CalculateLaplacian(mesh, sField);
            var lapRByElement = FiniteElementOperators.ProjectVertexFieldToElements(mesh, laplacianR);
            var lapSByElement = FiniteElementOperators.ProjectVertexFieldToElements(mesh, laplacianS);
            var gradR = FiniteElementOperators.CalculateElementWiseGradient(mesh, rField);
            var gradW = FiniteElementOperators.CalculateElementWiseGradient(mesh, wField);

            var l1 = new Dictionary<int, double>(mesh.ElementsTyped.Count);
            var l2 = new Dictionary<int, double>(mesh.ElementsTyped.Count);

            foreach (var element in mesh.ElementsTyped.Cast<FEMElement>())
            {
                var gradRValue = gradR.GetVector(element.Id);
                var gradWValue = gradW.GetVector(element.Id);
                double nonlinear = 2.0 * (gradRValue.X * gradWValue.X + gradRValue.Y * gradWValue.Y);

                double lapRValue = lapRByElement.TryGetValue(element.Id, out double rValue) ? rValue : 0.0;
                double lapSValue = lapSByElement.TryGetValue(element.Id, out double sValue) ? sValue : 0.0;

                l1[element.Id] = lapRValue + nonlinear;
                l2[element.Id] = lapSValue + nonlinear;
            }

            return (l1, l2);
        }

        /// <summary>
        /// Builds w = (r - s) / epsilon on the current vertex basis.
        /// </summary>
        public static PotentialDistribution BuildWField(PotentialDistribution rField,
                                                        PotentialDistribution sField,
                                                        double epsilon)
        {
            if (rField == null)
                throw new ArgumentNullException(nameof(rField));
            if (sField == null)
                throw new ArgumentNullException(nameof(sField));

            double safeEpsilon = Math.Abs(epsilon) < SmallDenominator ? SmallDenominator : epsilon;
            var values = rField.Potentials.ToDictionary(
                pair => pair.Key,
                pair => (pair.Value - sField.GetPotential(pair.Key)) / safeEpsilon);
            return new PotentialDistribution(values);
        }

        /// <summary>
        /// Recovers a nodal coefficient field a = -(Delta w + |grad w|^2) and
        /// optionally averages it over the supplied cycle of w-fields.
        /// </summary>
        public static PotentialDistribution RecoverCoefficientField(FEMMesh mesh,
                                                                    IReadOnlyList<PotentialDistribution> wFields,
                                                                    bool averageAcrossCycle)
        {
            if (mesh == null)
                throw new ArgumentNullException(nameof(mesh));
            if (wFields == null)
                throw new ArgumentNullException(nameof(wFields));
            if (wFields.Count == 0)
                return new PotentialDistribution(mesh.Vertices.ToDictionary(vertex => vertex.GlobalId, _ => 0.0));

            int frameCount = averageAcrossCycle ? wFields.Count : 1;
            var accum = mesh.Vertices.ToDictionary(vertex => vertex.GlobalId, _ => 0.0);

            foreach (var wField in wFields.Take(frameCount))
            {
                var laplacian = FiniteElementOperators.CalculateLaplacian(mesh, wField);
                var gradients = FiniteElementOperators.CalculateElementWiseGradient(mesh, wField);
                var gradientNormByElement = new Dictionary<int, double>(mesh.ElementsTyped.Count);
                foreach (var element in mesh.ElementsTyped.Cast<FEMElement>())
                {
                    var gradient = gradients.GetVector(element.Id);
                    gradientNormByElement[element.Id] = gradient.X * gradient.X + gradient.Y * gradient.Y;
                }

                var projectedGradientNorm = ProjectElementFieldToVertices(mesh, gradientNormByElement);
                foreach (var vertex in mesh.Vertices)
                {
                    double value = -(laplacian.GetPotential(vertex.GlobalId)
                                     + projectedGradientNorm.GetPotential(vertex.GlobalId));
                    accum[vertex.GlobalId] += value;
                }
            }

            foreach (int vertexId in accum.Keys.ToList())
                accum[vertexId] /= frameCount;

            return new PotentialDistribution(accum);
        }

        /// <summary>
        /// Applies a practical H1-like smoothing pass to the recovered coefficient
        /// field. This reduces the boundary-layer amplification produced by the
        /// recovered Laplacian on the current P1 basis before the V stage.
        /// </summary>
        public static PotentialDistribution SmoothRecoveredCoefficientField(FEMMesh mesh,
                                                                            Matrix laplacian,
                                                                            PotentialDistribution coefficientField,
                                                                            INumericSolver numericSolver,
                                                                            double smoothingWeight,
                                                                            double regularization)
        {
            if (mesh == null)
                throw new ArgumentNullException(nameof(mesh));
            if (laplacian == null)
                throw new ArgumentNullException(nameof(laplacian));
            if (coefficientField == null)
                throw new ArgumentNullException(nameof(coefficientField));
            if (numericSolver == null)
                throw new ArgumentNullException(nameof(numericSolver));
            if (smoothingWeight <= 0.0)
                return new PotentialDistribution(coefficientField.Potentials);

            int vertexCount = mesh.Vertices.Count;
            var system = DenseMatrix.Create(vertexCount, vertexCount, (row, column) => -smoothingWeight * laplacian[row, column]);
            var rhs = DenseVector.Create(vertexCount, index => coefficientField.GetPotential(mesh.Vertices[index].GlobalId));

            for (int index = 0; index < vertexCount; index++)
                system[index, index] += 1.0 + regularization;

            var solution = numericSolver.SolveLinearSystem(system, rhs);
            var values = new Dictionary<int, double>(vertexCount);
            for (int index = 0; index < vertexCount; index++)
                values[mesh.Vertices[index].GlobalId] = solution[index];

            return new PotentialDistribution(values);
        }

        /// <summary>
        /// Recovers the scale field V from Delta V + aV = 0 using a practical
        /// quasi-reversibility least-squares surrogate. Both V = 1 and d_n V = 0
        /// are enforced through penalty terms because the current P1 FEM basis is
        /// not a true H2/C1 space.
        /// </summary>
        public static PotentialDistribution RecoverScaleField(FEMMesh mesh,
                                                              Matrix laplacian,
                                                              PotentialDistribution coefficientField,
                                                              IReadOnlyList<FEMElectrode> electrodes,
                                                              INumericSolver numericSolver,
                                                              ConvexificationOptions options)
        {
            if (mesh == null)
                throw new ArgumentNullException(nameof(mesh));
            if (laplacian == null)
                throw new ArgumentNullException(nameof(laplacian));
            if (coefficientField == null)
                throw new ArgumentNullException(nameof(coefficientField));
            if (electrodes == null)
                throw new ArgumentNullException(nameof(electrodes));
            if (numericSolver == null)
                throw new ArgumentNullException(nameof(numericSolver));
            int vertexCount = mesh.Vertices.Count;
            var identity = DenseMatrix.CreateIdentity(vertexCount);
            var pdeOperator = DenseMatrix.Create(vertexCount,
                                                 vertexCount,
                                                 (row, column) => laplacian[row, column]);

            for (int index = 0; index < vertexCount; index++)
            {
                int vertexId = mesh.Vertices[index].GlobalId;
                pdeOperator[index, index] += coefficientField.GetPotential(vertexId);
            }

            // Sign convention note:
            // laplacian approximates Delta with positive off-diagonals and
            // negative row sums, i.e. (Delta_h u)_i ~ Σ_j w_ij (u_j - u_i).
            // Therefore Delta V + aV = 0 is assembled as
            // (L + diag(a)) V = 0 and the QRM residual term uses that operator
            // directly, not its negation.
            Matrix normalMatrix = pdeOperator.TransposeThisAndMultiply(pdeOperator) * options.VRecoveryResidualWeight;
            var rhs = DenseVector.Create(vertexCount, 0.0);

            if (options.VRecoveryGradientWeight > 0.0)
                normalMatrix += laplacian.Multiply(-options.VRecoveryGradientWeight);

            double massWeight = Math.Max(0.0, options.VRecoveryMassWeight);
            double regularization = Math.Max(0.0, options.SigmaRecoveryRegularization);
            if (massWeight > 0.0 || regularization > 0.0)
            {
                double diagonal = massWeight + regularization;
                normalMatrix += identity * diagonal;
                if (massWeight > 0.0)
                {
                    for (int index = 0; index < vertexCount; index++)
                        rhs[index] += massWeight;
                }
            }

            foreach (var boundaryVertex in mesh.GetOrderedBoundaryVertices())
            {
                int index = mesh.Vertices.FindIndex(vertex => vertex.GlobalId == boundaryVertex.GlobalId);
                if (index < 0)
                    continue;

                normalMatrix[index, index] += options.VRecoveryDirichletWeight;
                rhs[index] += options.VRecoveryDirichletWeight;
            }

            if (options.VRecoveryNeumannWeight > 0.0 && electrodes.Count > 0)
            {
                var neumannOperator = BuildElectrodeNormalDerivativeMatrix(mesh, electrodes);
                normalMatrix += neumannOperator.TransposeThisAndMultiply(neumannOperator) * options.VRecoveryNeumannWeight;
            }

            var solution = numericSolver.SolveLinearSystem(normalMatrix, rhs);
            var values = new Dictionary<int, double>(vertexCount);
            for (int index = 0; index < vertexCount; index++)
                values[mesh.Vertices[index].GlobalId] = Math.Max(options.MinimumScale, solution[index]);

            return new PotentialDistribution(values);
        }

        /// <summary>
        /// Converts a recovered scale field into an element-wise conductivity
        /// distribution sigma = V^2 by averaging vertex values on each element.
        /// </summary>
        public static ConductivityDistribution RecoverConductivity(FEMMesh mesh, PotentialDistribution scaleField)
        {
            if (mesh == null)
                throw new ArgumentNullException(nameof(mesh));
            if (scaleField == null)
                throw new ArgumentNullException(nameof(scaleField));

            var sigma = new Dictionary<int, double>(mesh.ElementsTyped.Count);
            foreach (var element in mesh.ElementsTyped.Cast<FEMElement>())
            {
                double average = element.Vertices.Average(vertex => scaleField.GetPotential(vertex.GlobalId));
                sigma[element.Id] = average * average;
            }

            return new ConductivityDistribution(sigma);
        }

        /// <summary>
        /// Blends two nodal fields using a scalar damping factor.
        /// </summary>
        public static PotentialDistribution Blend(PotentialDistribution baseline,
                                                  PotentialDistribution candidate,
                                                  double damping)
        {
            if (baseline == null)
                throw new ArgumentNullException(nameof(baseline));
            if (candidate == null)
                throw new ArgumentNullException(nameof(candidate));

            double alpha = Math.Clamp(damping, 0.0, 1.0);
            var values = baseline.Potentials.ToDictionary(
                pair => pair.Key,
                pair => (1.0 - alpha) * pair.Value + alpha * candidate.GetPotential(pair.Key));
            return new PotentialDistribution(values);
        }

        /// <summary>
        /// Applies a scaled increment to a nodal field.
        /// </summary>
        public static PotentialDistribution AddScaledIncrement(PotentialDistribution baseline,
                                                               PotentialDistribution increment,
                                                               double scale)
        {
            if (baseline == null)
                throw new ArgumentNullException(nameof(baseline));
            if (increment == null)
                throw new ArgumentNullException(nameof(increment));

            var values = baseline.Potentials.ToDictionary(
                pair => pair.Key,
                pair => pair.Value + scale * increment.GetPotential(pair.Key));
            return new PotentialDistribution(values);
        }

        /// <summary>
        /// Computes the relative change between two conductivity fields.
        /// </summary>
        public static double ComputeRelativeConductivityChange(ConductivityDistribution previous,
                                                               ConductivityDistribution current)
        {
            if (previous == null)
                throw new ArgumentNullException(nameof(previous));
            if (current == null)
                throw new ArgumentNullException(nameof(current));

            double numerator = 0.0;
            double denominator = 0.0;
            foreach (var pair in current.Conductivities)
            {
                double before = previous.GetConductivity(pair.Key);
                double delta = pair.Value - before;
                numerator += delta * delta;
                denominator += before * before;
            }

            return Math.Sqrt(numerator) / Math.Max(1e-12, Math.Sqrt(denominator));
        }

        private static int NormalizeStep(int stepIndex, int cycleLength)
        {
            if (cycleLength <= 0)
                return stepIndex;

            int normalized = stepIndex % cycleLength;
            return normalized < 0 ? normalized + cycleLength : normalized;
        }

        private static double DifferentiateAt(IReadOnlyList<OrderedFrame> ordered,
                                              IReadOnlyList<double[]> smoothedSamples,
                                              int frameIndex,
                                              int electrodeIndex,
                                              int cycleLength,
                                              bool usePeriodic)
        {
            if (ordered.Count == 1)
                return 0.0;

            if (usePeriodic)
            {
                int previousIndex = (frameIndex - 1 + ordered.Count) % ordered.Count;
                int nextIndex = (frameIndex + 1) % ordered.Count;
                double previousStep = ordered[previousIndex].StepIndex;
                double nextStep = ordered[nextIndex].StepIndex;

                if (previousIndex > frameIndex)
                    previousStep -= cycleLength;
                if (nextIndex < frameIndex)
                    nextStep += cycleLength;

                return SafeDifference(smoothedSamples[nextIndex][electrodeIndex],
                                      smoothedSamples[previousIndex][electrodeIndex],
                                      nextStep - previousStep);
            }

            if (frameIndex == 0)
            {
                return SafeDifference(smoothedSamples[1][electrodeIndex],
                                      smoothedSamples[0][electrodeIndex],
                                      ordered[1].StepIndex - ordered[0].StepIndex);
            }

            if (frameIndex == ordered.Count - 1)
            {
                return SafeDifference(smoothedSamples[frameIndex][electrodeIndex],
                                      smoothedSamples[frameIndex - 1][electrodeIndex],
                                      ordered[frameIndex].StepIndex - ordered[frameIndex - 1].StepIndex);
            }

            return SafeDifference(smoothedSamples[frameIndex + 1][electrodeIndex],
                                  smoothedSamples[frameIndex - 1][electrodeIndex],
                                  ordered[frameIndex + 1].StepIndex - ordered[frameIndex - 1].StepIndex);
        }

        private static double SafeDifference(double upper, double lower, double delta)
        {
            if (Math.Abs(delta) < SmallDenominator || !double.IsFinite(upper) || !double.IsFinite(lower))
                return 0.0;

            return (upper - lower) / delta;
        }

        private static List<double[]> SmoothOrderedSamples(IReadOnlyList<OrderedFrame> ordered,
                                                           int signalLength,
                                                           int smoothingWindow,
                                                           int smoothingPasses,
                                                           bool usePeriodic)
        {
            var smoothed = ordered
                .Select(frame => (double[])frame.Values.Clone())
                .ToList();

            int window = NormalizeSmoothingWindow(smoothingWindow);
            if (window <= 1 || smoothingPasses <= 0)
                return smoothed;

            int halfWindow = window / 2;
            for (int pass = 0; pass < smoothingPasses; pass++)
            {
                var next = Enumerable.Range(0, smoothed.Count)
                    .Select(_ => new double[signalLength])
                    .ToList();

                for (int frameIndex = 0; frameIndex < smoothed.Count; frameIndex++)
                {
                    for (int electrodeIndex = 0; electrodeIndex < signalLength; electrodeIndex++)
                    {
                        double weightedSum = 0.0;
                        double totalWeight = 0.0;

                        for (int offset = -halfWindow; offset <= halfWindow; offset++)
                        {
                            int sampleIndex = frameIndex + offset;
                            if (usePeriodic)
                            {
                                sampleIndex %= smoothed.Count;
                                if (sampleIndex < 0)
                                    sampleIndex += smoothed.Count;
                            }
                            else if (sampleIndex < 0 || sampleIndex >= smoothed.Count)
                            {
                                continue;
                            }

                            double weight = halfWindow + 1 - Math.Abs(offset);
                            weightedSum += weight * smoothed[sampleIndex][electrodeIndex];
                            totalWeight += weight;
                        }

                        next[frameIndex][electrodeIndex] = totalWeight > 0.0
                            ? weightedSum / totalWeight
                            : smoothed[frameIndex][electrodeIndex];
                    }
                }

                smoothed = next;
            }

            return smoothed;
        }

        private static int NormalizeSmoothingWindow(int smoothingWindow)
        {
            if (smoothingWindow <= 1)
                return smoothingWindow;

            return smoothingWindow % 2 == 0 ? smoothingWindow + 1 : smoothingWindow;
        }

        private static PotentialDistribution ToPotentialDistribution(FEMMesh mesh, IReadOnlyList<double> values)
        {
            var distribution = new Dictionary<int, double>(mesh.Vertices.Count);
            for (int index = 0; index < mesh.Vertices.Count; index++)
                distribution[mesh.Vertices[index].GlobalId] = values[index];

            return new PotentialDistribution(distribution);
        }

        private static IEnumerable<int> GetElectrodeVertexIds(FEMElectrode electrode)
        {
            if (electrode.FEMVertexIds.Count > 0)
                return electrode.FEMVertexIds;
            if (electrode.MeshId >= 0)
                return new[] { electrode.MeshId };

            return [];
        }

        private static double[] BuildBoundaryDescentValues(ConvexificationBoundaryData boundaryData,
                                                           IReadOnlyList<double> dirichletMismatch,
                                                           IReadOnlyList<double> neumannMismatch,
                                                           IReadOnlyList<FEMElectrode> electrodes,
                                                           double h,
                                                           ConvexificationOptions options)
        {
            double dirichletScale = options.BoundaryDirichletWeight / Math.Pow(Math.Max(h, options.ElectrodeLengthFloor), 3);
            double neumannScale = options.BoundaryNeumannWeight / Math.Max(h, options.ElectrodeLengthFloor);
            double normalizer = Math.Max(1.0, dirichletScale + neumannScale);

            var values = new double[dirichletMismatch.Count];
            for (int electrodeIndex = 0; electrodeIndex < values.Length; electrodeIndex++)
            {
                double boundaryGradient = dirichletScale * dirichletMismatch[electrodeIndex];
                if (ShouldApplyNeumannPenalty(boundaryData, electrodeIndex, options))
                    boundaryGradient += neumannScale * h * neumannMismatch[electrodeIndex];

                values[electrodeIndex] = -boundaryGradient / normalizer;
            }

            return values;
        }

        private static bool ShouldApplyNeumannPenalty(ConvexificationBoundaryData boundaryData,
                                                      int electrodeIndex,
                                                      ConvexificationOptions options)
        {
            if (options.UseAllElectrodesForNeumannPenalty || boundaryData.PatternStep == null)
                return true;

            int excitation = boundaryData.PatternStep.Excitation.First;
            int ground = boundaryData.PatternStep.Excitation.Second;
            return electrodeIndex != excitation && electrodeIndex != ground;
        }

        private static bool TryGetElectrodeOutwardNormal(FEMMesh mesh,
                                                         IReadOnlyCollection<int> contactIds,
                                                         out double nx,
                                                         out double ny)
        {
            nx = 0.0;
            ny = 0.0;

            var boundary = mesh.GetOrderedBoundaryVertices();
            if (contactIds.Count == 0 || boundary.Count == 0)
                return false;

            double boundaryCx = boundary.Average(vertex => vertex.X);
            double boundaryCy = boundary.Average(vertex => vertex.Y);

            double ex = 0.0;
            double ey = 0.0;
            foreach (int id in contactIds)
            {
                var vertex = mesh.GetVertexById(id);
                ex += vertex.X;
                ey += vertex.Y;
            }

            ex /= contactIds.Count;
            ey /= contactIds.Count;

            nx = ex - boundaryCx;
            ny = ey - boundaryCy;
            double norm = Math.Sqrt(nx * nx + ny * ny);
            if (norm < SmallDenominator)
                return false;

            nx /= norm;
            ny /= norm;
            return true;
        }

        private static double ComputeFieldNorm(PotentialDistribution field)
        {
            double sum = 0.0;
            foreach (double value in field.Potentials.Values)
                sum += value * value;

            return Math.Sqrt(sum);
        }

        private static double ResolveElectrodeLength(FEMElectrode electrode, double floor)
        {
            if (electrode == null)
                return floor;

            double length = electrode.Length;
            if (!double.IsFinite(length) || length <= floor)
                return floor;

            return length;
        }

        private static Dictionary<long, double> BuildCotangentWeights(FEMMesh mesh)
        {
            var weights = new Dictionary<long, double>();
            foreach (var element in mesh.ElementsTyped.Cast<FEMElement>())
            {
                var a = element.Vertices[0];
                var b = element.Vertices[1];
                var c = element.Vertices[2];

                AddCotangentWeight(weights, b.GlobalId, c.GlobalId, Cotangent(b, c, a));
                AddCotangentWeight(weights, c.GlobalId, a.GlobalId, Cotangent(c, a, b));
                AddCotangentWeight(weights, a.GlobalId, b.GlobalId, Cotangent(a, b, c));
            }

            return weights;
        }

        private static void AddCotangentWeight(Dictionary<long, double> weights, int first, int second, double value)
        {
            long key = PackKey(Math.Min(first, second), Math.Max(first, second));
            if (weights.TryGetValue(key, out double existing))
                weights[key] = existing + value;
            else
                weights[key] = value;
        }

        private static long PackKey(int first, int second)
            => ((long)first << 32) | (uint)second;

        private static double Cotangent(FEMVertex p1, FEMVertex p2, FEMVertex p3)
        {
            double v1x = p1.X - p3.X;
            double v1y = p1.Y - p3.Y;
            double v2x = p2.X - p3.X;
            double v2y = p2.Y - p3.Y;

            double dot = v1x * v2x + v1y * v2y;
            double cross = v1x * v2y - v1y * v2x;
            return Math.Abs(cross) < SmallDenominator ? 0.0 : dot / cross;
        }

        private sealed record OrderedFrame(int OriginalIndex, int StepIndex, double[] Values);
    }
}
