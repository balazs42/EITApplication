using System.Collections.Concurrent;
using System.Numerics;
using MathNet.Numerics.LinearAlgebra;
using MathNet.Numerics.LinearAlgebra.Double;
using Utility.Classes.Discretizer;
using Utility.Classes.Discretizer.FiniteElementMesh;
using Utility.Classes.Measurement;
using Utility.Classes.Reconstruction;
using Utility.Classes.ReconstructionParameters;

using Vector = MathNet.Numerics.LinearAlgebra.Vector<double>;
using Matrix = MathNet.Numerics.LinearAlgebra.Matrix<double>;

namespace Utility.Classes.Solvers.FiniteElementSolver
{
    /// <summary>
    /// Core FEM engine for the Complete Electrode Model (CEM).
    /// Equations referenced inline:
    ///  • Eq (1.2.3): stiffness K
    ///  • Eq (1.1.12): contact impedance M, coupling A_coup
    ///  • Eq (1.1.15): electrode diag D
    ///  • Eq (1.1.16): assemble block system S
    ///  • Sec 1.1.3: grounding removal
    ///  • Eq (2.1.20): gradient if needed
    /// </summary>
    public sealed class FiniteElementSolver : ISolver
    {
        private readonly INumericSolver _numericSolver;
        private readonly bool _useOmpParallelization;
        private FEMMesh _referenceMesh;

        private readonly Dictionary<int, IReadOnlyList<int>> _electrodeContactCache = [];
        private readonly Dictionary<int, List<(int start, int end, double length)>> _segmentCache = [];
        private readonly object _cacheGuard = new();
        private bool _boundaryMatricesDirty = true;
        private double[] _cachedContactImpedances;

        private Matrix _stiffnessMatrix;
        private Matrix _robinMassMatrix;
        private Matrix _couplingMatrix;
        private Vector _electrodeDiagonal;
        private Matrix _systemMatrix;
        private Vector _systemRhs;
        private int _groundElectrodeId;
        private int _referenceNodeId = 0;   // Reference node is treated as the 0 potential node so everything is shifted by its value.

        public int N_phi { get; }
        public int L { get; }

        // Sub-block matrices
        public Matrix K => _stiffnessMatrix;
        public Matrix M => _robinMassMatrix;
        public Matrix A_coup => _couplingMatrix;
        public Vector D => _electrodeDiagonal;

        // Global system
        public Matrix SystemMatrix => _systemMatrix;
        public Vector SystemRHS => _systemRhs;

        /// <summary>
        /// Initialize solver with mesh sizes and numeric solver.
        /// </summary>
        public FiniteElementSolver(FEMMesh mesh, INumericSolver numericSolver, bool useOmpParallelization = false)
        {
            N_phi = mesh.Vertices.Count;
            L = mesh.GetElectrodes().Count;
            _numericSolver = numericSolver ?? throw new ArgumentNullException(nameof(numericSolver));
            _useOmpParallelization = useOmpParallelization;

            _referenceMesh = mesh;

            _stiffnessMatrix = SparseMatrix.Create(N_phi, N_phi, 0.0);
            _robinMassMatrix = SparseMatrix.Create(N_phi, N_phi, 0.0);
            _couplingMatrix = SparseMatrix.Create(N_phi, L, 0.0);
            _electrodeDiagonal = Vector.Build.Dense(L, 0.0);
            _systemMatrix = SparseMatrix.Create(N_phi + Math.Max(0, L - 1), N_phi + Math.Max(0, L - 1), 0.0);
            _systemRhs = Vector.Build.Sparse(N_phi + Math.Max(0, L - 1));
            _cachedContactImpedances = Enumerable.Repeat(double.NaN, L).ToArray();
            _groundElectrodeId = 0;
            _referenceNodeId = 0;
        }

        /// <summary>
        /// Solves the forward CEM problem on the provided discretization by
        /// assembling the block saddle-point system and applying the configured
        /// numeric linear solver.
        /// </summary>
        /// <param name="discretization">Finite-element discretization.</param>
        /// <param name="boundaryCondition">Boundary condition carrying electrode drives.</param>
        /// <returns>Computed potential distribution.</returns>
        public PotentialDistribution SolveForward(IDiscretization discretization, BoundaryCondition boundaryCondition)
        {
            var femMesh = discretization as FEMMesh ?? throw new InvalidCastException();
            var bc = boundaryCondition as FEMBoundaryCondition ?? throw new InvalidCastException();
            var bcElectrodes = bc.GetElectrodes();

            return Solve(femMesh, bcElectrodes);
        }

        /// <summary>
        /// Reuses the forward assembly to solve the adjoint CEM problem driven
        /// by an electrode source vector.  This feeds reconstruction
        /// algorithms that require ∇·(σ∇λ) solves.
        /// </summary>
        /// <param name="discretization">Finite-element discretization.</param>
        /// <param name="boundaryCondition">Boundary condition providing electrode metadata.</param>
        /// <param name="adjointSource">Adjoint current density per electrode.</param>
        /// <returns>Adjoint potential distribution.</returns>
        public PotentialDistribution SolveAdjoint(IDiscretization discretization, BoundaryCondition boundaryCondition, Complex[] adjointSource)
        {
            var femMesh = discretization as FEMMesh ?? throw new InvalidCastException();
            var bc = boundaryCondition as FEMBoundaryCondition ?? throw new InvalidCastException();
            var electrodes = femMesh.GetElectrodes();
            var bcElectrodes = bc.GetElectrodes();
            int bcElectrodeCount = bcElectrodes.Count;

            for (int i = 0; i < bcElectrodeCount; i++)
            {
                electrodes[i].Potential = 0.0;  
                bcElectrodes[i].Potential = 0.0;

                electrodes[i].Current = adjointSource[i].Real;  // TODO: add complex currents
                bcElectrodes[i].Current = adjointSource[i].Real;
            }

            return Solve(femMesh, bcElectrodes);
        }

        /// <summary>
        /// Solve forward CEM problem by assembling the sparse saddle point system,
        /// removing the grounded electrode degree of freedom during assembly and solving
        /// the reduced system for node and electrode potentials.
        /// </summary>
        /// <param name="mesh"/> FEM mesh
        /// <param name="electrodes"/> electrode list with .Current, .IsGround set
        /// <returns>vector [alpha; U]</returns>
        private PotentialDistribution Solve(FEMMesh mesh, List<FEMElectrode> electrodes)
        {
            AssembleSystem(mesh, electrodes);

            var solution = _numericSolver.SolveLinearSystem(_systemMatrix, _systemRhs);

            // --- 1) get potential at reference node (EIDORS gnd_node) ---
            if (_referenceNodeId < 0 || _referenceNodeId >= N_phi)
                throw new InvalidOperationException(
                    $"Reference node id {_referenceNodeId} out of range [0, {N_phi - 1}]");

            double referencePotential = solution[_referenceNodeId];
            _ = referencePotential;

            // --- 2) build node potentials shifted so that φ(referenceNode) = 0 ---
            var nodePotentials = new double[N_phi];
            for (int i = 0; i < N_phi; i++)
                nodePotentials[i] = Sanitize(solution[i]);// - referencePotential;

            var potentialDistribution = PotentialDistribution.FromDense(nodePotentials, 0, takeOwnership: true);
            mesh.ApplySolvedPotentialDistribution(potentialDistribution);

            void UpdateElectrodePotentials(IReadOnlyList<FEMElectrode> list)
            {
                for (int i = 0; i < list.Count; i++)
                {
                    var el = list[i];
                    double potential = (L <= 1 || el.Id == _groundElectrodeId)
                        ? 0.0
                        : solution[ElectrodeColumn(el.Id)];
                    el.Potential = Sanitize(potential);
                }
            }

            UpdateElectrodePotentials(electrodes);
            UpdateElectrodePotentials(mesh.ElectrodesTyped);

            return potentialDistribution;
        }

        private static double Sanitize(double value) => double.IsFinite(value) ? value : 0.0;


#region Assembly

        private void AssembleSystem(FEMMesh mesh, List<FEMElectrode> electrodes)
        {
            if (mesh == null)
                throw new ArgumentNullException(nameof(mesh));
            if (electrodes == null)
                throw new ArgumentNullException(nameof(electrodes));
            if (electrodes.Count == 0)
                throw new InvalidOperationException("The mesh does not define any electrodes.");

            EnsureMeshCaches(mesh);

            _groundElectrodeId = electrodes.Find(e => e.IsGround)?.Id ?? 0;
            if (_groundElectrodeId < 0 || _groundElectrodeId >= electrodes.Count)
                _groundElectrodeId = 0;

            if (electrodes.Count != L)
                throw new InvalidOperationException("Electrode count changed after solver initialisation.");

            BuildStiffnessMatrix(mesh);
            BuildBoundaryMatrices(mesh, electrodes);
            BuildSystemMatrix(electrodes);
            BuildRhsVector(electrodes);
        }

        private void EnsureMeshCaches(FEMMesh mesh)
        {
            if (ReferenceEquals(mesh, _referenceMesh))
                return;

            lock (_cacheGuard)
            {
                _electrodeContactCache.Clear();
                _segmentCache.Clear();
                _boundaryMatricesDirty = true;
                _referenceMesh = mesh;
                _cachedContactImpedances = Enumerable.Repeat(double.NaN, mesh.GetElectrodes().Count).ToArray();
            }
        }

        private void BuildStiffnessMatrix(FEMMesh mesh)
        {
            var sigma = mesh.GetConductivityDistribution();
            var elements = mesh.ElementsTyped;
            int elementCount = elements.Count;
            int estimatedContributionCount = Math.Max(elementCount * 9, 16);

            Dictionary<long, double> contributions;

            if (_useOmpParallelization && elementCount > 1)
            {
                var bag = new ConcurrentBag<Dictionary<long, double>>();
                int workerCount = Math.Min(Environment.ProcessorCount, elementCount);
                int localCapacity = Math.Max(estimatedContributionCount / Math.Max(workerCount, 1), 32);

                Parallel.ForEach(Partitioner.Create(0, elementCount),
                    new ParallelOptions { MaxDegreeOfParallelism = workerCount },
                    () => new Dictionary<long, double>(localCapacity),
                    (range, _, local) =>
                    {
                        for (int i = range.Item1; i < range.Item2; i++)
                            AccumulateElementStiffness(elements[i], sigma, local);
                        return local;
                    },
                    local => bag.Add(local));

                contributions = new Dictionary<long, double>(estimatedContributionCount);
                foreach (var local in bag)
                    MergeContributions(contributions, local);
            }
            else
            {
                contributions = new Dictionary<long, double>(estimatedContributionCount);
                for (int i = 0; i < elementCount; i++)
                    AccumulateElementStiffness(elements[i], sigma, contributions);
            }

            _stiffnessMatrix = SparseMatrix.OfIndexed(N_phi, N_phi, EnumerateContributions(contributions));
        }

        private void BuildBoundaryMatrices(FEMMesh mesh, List<FEMElectrode> electrodes)
        {
            bool needsRebuild = _boundaryMatricesDirty;
            for (int ell = 0; ell < electrodes.Count; ell++)
            {
                double z = electrodes[ell].ZContact;
                if (!double.Equals(z, _cachedContactImpedances[ell]))
                {
                    needsRebuild = true;
                    _cachedContactImpedances[ell] = z;
                }
            }

            if (!needsRebuild)
                return;

            var massContrib = new Dictionary<long, double>(Math.Max(16, electrodes.Count * 8));
            var coupContrib = new Dictionary<long, double>(Math.Max(16, electrodes.Count * 4));
            var diag = new double[electrodes.Count];

            for (int ell = 0; ell < electrodes.Count; ell++)
                AccumulateElectrodeMatrices(mesh, electrodes[ell], ell, massContrib, coupContrib, diag);

            _robinMassMatrix = SparseMatrix.OfIndexed(N_phi, N_phi, EnumerateContributions(massContrib));
            _couplingMatrix = SparseMatrix.OfIndexed(N_phi, electrodes.Count, EnumerateContributions(coupContrib));
            _electrodeDiagonal = Vector.Build.Dense(diag);
            _boundaryMatricesDirty = false;
        }

        private void BuildSystemMatrix(List<FEMElectrode> electrodes)
        {
            int systemSize = N_phi + Math.Max(0, L - 1);
            var contributions = new Dictionary<long, double>();

            foreach (var (row, col, value) in _stiffnessMatrix.EnumerateIndexed(Zeros.AllowSkip))
                AddContribution(contributions, row, col, value);

            foreach (var (row, col, value) in _robinMassMatrix.EnumerateIndexed(Zeros.AllowSkip))
                AddContribution(contributions, row, col, value);

            foreach (var (row, col, value) in _couplingMatrix.EnumerateIndexed(Zeros.AllowSkip))
            {
                if (col == _groundElectrodeId)
                    continue;

                int c = ElectrodeColumn(col);
                AddContribution(contributions, row, c, -value);
                AddContribution(contributions, c, row, -value);
            }

            for (int ell = 0; ell < L; ell++)
            {
                if (ell == _groundElectrodeId)
                    continue;

                int c = ElectrodeColumn(ell);
                AddContribution(contributions, c, c, _electrodeDiagonal[ell]);
            }

            _systemMatrix = SparseMatrix.OfIndexed(systemSize, systemSize, EnumerateContributions(contributions));
        }

        private void BuildRhsVector(List<FEMElectrode> electrodes)
        {
            int systemSize = N_phi + Math.Max(0, L - 1);
            _systemRhs = Vector.Build.Sparse(systemSize);

            for (int ell = 0; ell < L; ell++)
            {
                if (ell == _groundElectrodeId)
                    continue;

                double current = electrodes[ell].Current;
                if (Math.Abs(current) < 1e-30)
                    continue;

                _systemRhs[ElectrodeColumn(ell)] = current;
            }
        }

        private int ElectrodeColumn(int electrodeId)
            => electrodeId < _groundElectrodeId ? N_phi + electrodeId : N_phi + electrodeId - 1;

        private static void AccumulateElementStiffness(FEMElement elem, ConductivityDistribution sigma, Dictionary<long, double> target)
        {
            double area = elem.Area;
            double conductivity = sigma.GetConductivity(elem.Id);
            if (conductivity == 0.0 || area <= 0.0)
                return;

            var grads = elem.GradPhi;
            for (int i = 0; i < 3; i++)
            {
                int row = elem.Vertices[i].GlobalId;
                for (int j = 0; j < 3; j++)
                {
                    int col = elem.Vertices[j].GlobalId;
                    double gdot = grads[i][0] * grads[j][0] + grads[i][1] * grads[j][1];
                    AddContribution(target, row, col, conductivity * area * gdot);
                }
            }
        }

        private void AccumulateElectrodeMatrices(
            FEMMesh mesh,
            FEMElectrode electrode,
            int electrodeIndex,
            Dictionary<long, double> massTarget,
            Dictionary<long, double> couplingTarget,
            double[] diag)
        {
            if (electrode.ZContact <= 0.0)
                return;

            var contactVertexIds = GetContactVertexIdsCached(mesh, electrode);
            double invZ = 1.0 / electrode.ZContact;

            if (!electrode.PointElectrode && contactVertexIds.Count >= 2)
            {
                var segments = GetElectrodeSegments(mesh, electrode, contactVertexIds);
                double totalLength = 0.0;
                foreach (var (start, end, length) in segments)
                {
                    if (length <= 0.0)
                        continue;

                    totalLength += length;
                    double diagVal = invZ * length / 3.0;
                    double offVal = invZ * length / 6.0;
                    AddContribution(massTarget, start, start, diagVal);
                    AddContribution(massTarget, end, end, diagVal);
                    AddContribution(massTarget, start, end, offVal);
                    AddContribution(massTarget, end, start, offVal);

                    double coupVal = invZ * length / 2.0;
                    AddContribution(couplingTarget, start, electrodeIndex, coupVal);
                    AddContribution(couplingTarget, end, electrodeIndex, coupVal);
                }

                if (totalLength > 0.0)
                {
                    electrode.Length = totalLength;
                    diag[electrodeIndex] += totalLength * invZ;
                    return;
                }
            }

            double lengthFallback = ResolveElectrodeLength(mesh, electrode, [.. contactVertexIds]);
            if (lengthFallback <= 0.0)
                lengthFallback = 1e-6;

            double average = lengthFallback / Math.Max(1, contactVertexIds.Count);
            double massValue = invZ * average;
            foreach (int vid in contactVertexIds)
            {
                AddContribution(massTarget, vid, vid, massValue);
                AddContribution(couplingTarget, vid, electrodeIndex, massValue);
            }

            diag[electrodeIndex] += lengthFallback * invZ;
        }

        private static void AddContribution(Dictionary<long, double> map, int row, int col, double value)
        {
            long key = PackKey(row, col);
            if (map.TryGetValue(key, out double existing))
                map[key] = existing + value;
            else
                map[key] = value;
        }

        private static void MergeContributions(Dictionary<long, double> target, Dictionary<long, double> source)
        {
            foreach (var kv in source)
            {
                if (target.TryGetValue(kv.Key, out double existing))
                    target[kv.Key] = existing + kv.Value;
                else
                    target[kv.Key] = kv.Value;
            }
        }

        private static IEnumerable<Tuple<int, int, double>> EnumerateContributions(Dictionary<long, double> contributions)
        {
            foreach (var kv in contributions)
            {
                int row = (int)(kv.Key >> 32);
                int col = (int)(kv.Key & 0xFFFFFFFF);
                yield return Tuple.Create(row, col, kv.Value);
            }
        }

        private static long PackKey(int row, int col) => ((long)row << 32) | (uint)col;

        private IReadOnlyList<int> GetContactVertexIdsCached(FEMMesh mesh, FEMElectrode electrode)
        {
            lock (_cacheGuard)
            {
                if (_electrodeContactCache.TryGetValue(electrode.Id, out var cached))
                    return cached;

                var computed = BuildContactVertexIds(mesh, electrode);
                _electrodeContactCache[electrode.Id] = computed;
                _boundaryMatricesDirty = true;
                return computed;
            }
        }

        private IReadOnlyList<int> BuildContactVertexIds(FEMMesh mesh, FEMElectrode electrode)
        {
            if (electrode.FEMVertexIds != null && electrode.FEMVertexIds.Count > 0)
                return mesh.OrderVerticesAlongBoundary(electrode.FEMVertexIds);

            if (electrode.MeshId >= 0)
                return new List<int> { electrode.MeshId };

            throw new InvalidOperationException($"Electrode {electrode.Id} does not reference any FEM vertex.");
        }

        private List<(int StartId, int EndId, double Length)> GetElectrodeSegments(
            FEMMesh mesh,
            FEMElectrode electrode,
            IReadOnlyList<int> orderedVertexIds)
        {
            lock (_cacheGuard)
            {
                if (_segmentCache.TryGetValue(electrode.Id, out var cached))
                    return cached;

                var segments = BuildElectrodeSegments(mesh, orderedVertexIds.ToList());
                _segmentCache[electrode.Id] = segments;
                return segments;
            }
        }

#endregion

        #region Utils

    private List<(int StartId, int EndId, double Length)> BuildElectrodeSegments(FEMMesh mesh, List<int> orderedVertexIds)
    {
        var segments = new List<(int, int, double)>();
        if (orderedVertexIds == null || orderedVertexIds.Count < 2)
            return segments;

        for (int i = 0; i < orderedVertexIds.Count - 1; i++)
        {
            var start = mesh.GetVertexById(orderedVertexIds[i]);
            var end = mesh.GetVertexById(orderedVertexIds[i + 1]);
            double dx = start.X - end.X;
            double dy = start.Y - end.Y;
            double length = Math.Sqrt(dx * dx + dy * dy);
            if (length > 0.0)
                segments.Add((start.GlobalId, end.GlobalId, length));
        }
        return segments;
    }

        private double ResolveElectrodeLength(FEMMesh mesh, FEMElectrode electrode, List<int> contactVertexIds)
        {
            double length = electrode.Length;
            if (length > 0.0)
                return length;

            if (contactVertexIds != null && contactVertexIds.Count > 0)
                length = mesh.ComputeElectrodeLength(contactVertexIds);

            if ((length <= 0.0 || double.IsNaN(length)) && electrode.MeshId >= 0)
                length = mesh.ComputeElectrodeLength(new List<int> { electrode.MeshId });

            if (length <= 0.0 || double.IsNaN(length))
                length = ComputeAverageBoundarySpacing(mesh);

            if (length <= 0.0 || double.IsNaN(length))
                length = 1e-6;

            electrode.Length = length;
            return length;
        }

        private static double ComputeAverageBoundarySpacing(FEMMesh mesh)
        {
            var boundary = mesh.GetOrderedBoundaryVertices();
            int count = boundary.Count;
            if (count < 2)
                return 0.0;

            double total = 0.0;
            for (int i = 0; i < count; i++)
            {
                var a = boundary[i];
                var b = boundary[(i + 1) % count];
                double dx = a.X - b.X;
                double dy = a.Y - b.Y;
                total += Math.Sqrt(dx * dx + dy * dy);
            }

            return total / count;
        }

        #endregion
    }
}
