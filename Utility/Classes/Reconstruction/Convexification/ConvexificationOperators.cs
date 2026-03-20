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

            var derivativeBySortedIndex = new double[ordered.Count][];
            for (int i = 0; i < ordered.Count; i++)
                derivativeBySortedIndex[i] = new double[signalLength];

            for (int i = 0; i < ordered.Count; i++)
            {
                for (int electrode = 0; electrode < signalLength; electrode++)
                {
                    derivativeBySortedIndex[i][electrode] = DifferentiateAt(ordered,
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

            double nx = ex - boundaryCx;
            double ny = ey - boundaryCy;
            double norm = Math.Sqrt(nx * nx + ny * ny);
            if (norm < SmallDenominator)
                return 0.0;

            nx /= norm;
            ny /= norm;

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
        /// Recovers the scale field V from Delta V + aV = 0 with V = 1 enforced
        /// on the boundary through a discrete Dirichlet solve.
        /// </summary>
        public static PotentialDistribution RecoverScaleField(FEMMesh mesh,
                                                              Matrix laplacian,
                                                              PotentialDistribution coefficientField,
                                                              INumericSolver numericSolver,
                                                              double regularization,
                                                              double minimumScale)
        {
            if (mesh == null)
                throw new ArgumentNullException(nameof(mesh));
            if (laplacian == null)
                throw new ArgumentNullException(nameof(laplacian));
            if (coefficientField == null)
                throw new ArgumentNullException(nameof(coefficientField));
            if (numericSolver == null)
                throw new ArgumentNullException(nameof(numericSolver));

            var boundaryValues = mesh.GetOrderedBoundaryVertices()
                .ToDictionary(vertex => vertex.GlobalId, _ => 1.0);

            int vertexCount = mesh.Vertices.Count;
            var idToIndex = mesh.Vertices
                .Select((vertex, index) => (vertex.GlobalId, index))
                .ToDictionary(item => item.GlobalId, item => item.index);
            var indexToId = mesh.Vertices.Select(vertex => vertex.GlobalId).ToArray();

            var boundaryIndices = boundaryValues.Keys
                .Where(idToIndex.ContainsKey)
                .Select(id => idToIndex[id])
                .OrderBy(index => index)
                .ToArray();
            var interiorIndices = Enumerable.Range(0, vertexCount)
                .Where(index => !boundaryIndices.Contains(index))
                .ToArray();

            var values = Enumerable.Repeat(1.0, vertexCount).ToArray();
            if (interiorIndices.Length == 0)
                return ToPotentialDistribution(mesh, values);

            var system = DenseMatrix.Create(interiorIndices.Length, interiorIndices.Length, 0.0);
            var rhs = DenseVector.Create(interiorIndices.Length, 0.0);

            for (int row = 0; row < interiorIndices.Length; row++)
            {
                int globalRow = interiorIndices[row];
                int vertexId = indexToId[globalRow];

                for (int column = 0; column < interiorIndices.Length; column++)
                    system[row, column] = laplacian[globalRow, interiorIndices[column]];

                system[row, row] += coefficientField.GetPotential(vertexId) - regularization;

                for (int column = 0; column < boundaryIndices.Length; column++)
                {
                    int boundaryIndex = boundaryIndices[column];
                    rhs[row] -= laplacian[globalRow, boundaryIndex] * values[boundaryIndex];
                }
            }

            var solution = numericSolver.SolveLinearSystem(system, rhs);
            for (int row = 0; row < interiorIndices.Length; row++)
                values[interiorIndices[row]] = Math.Max(minimumScale, solution[row]);

            return ToPotentialDistribution(mesh, values);
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

        private static int NormalizeStep(int stepIndex, int cycleLength)
        {
            if (cycleLength <= 0)
                return stepIndex;

            int normalized = stepIndex % cycleLength;
            return normalized < 0 ? normalized + cycleLength : normalized;
        }

        private static double DifferentiateAt(IReadOnlyList<OrderedFrame> ordered,
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

                return SafeDifference(ordered[nextIndex].Values[electrodeIndex],
                                      ordered[previousIndex].Values[electrodeIndex],
                                      nextStep - previousStep);
            }

            if (frameIndex == 0)
            {
                return SafeDifference(ordered[1].Values[electrodeIndex],
                                      ordered[0].Values[electrodeIndex],
                                      ordered[1].StepIndex - ordered[0].StepIndex);
            }

            if (frameIndex == ordered.Count - 1)
            {
                return SafeDifference(ordered[frameIndex].Values[electrodeIndex],
                                      ordered[frameIndex - 1].Values[electrodeIndex],
                                      ordered[frameIndex].StepIndex - ordered[frameIndex - 1].StepIndex);
            }

            return SafeDifference(ordered[frameIndex + 1].Values[electrodeIndex],
                                  ordered[frameIndex - 1].Values[electrodeIndex],
                                  ordered[frameIndex + 1].StepIndex - ordered[frameIndex - 1].StepIndex);
        }

        private static double SafeDifference(double upper, double lower, double delta)
        {
            if (Math.Abs(delta) < SmallDenominator || !double.IsFinite(upper) || !double.IsFinite(lower))
                return 0.0;

            return (upper - lower) / delta;
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
