using Utility.Classes.Factories;
using System.Linq;
using Utility.Classes.Reconstruction.VirtualElectrodes;

namespace Utility.Classes.Discretizer.FiniteElementMesh
{
    /// <summary>
    /// Triangular 2D FEM mesh with support for electrodes and boundary handling.
    /// - Holds vertices, elements, and electrodes.
    /// - Builds edge and boundary topology from elements.
    /// - Orders boundary vertices and can place electrodes equidistantly along the boundary.
    /// - Computes electrode physical lengths from mesh geometry.
    /// - Supports uniform refinement (each triangle split into 4) and conversion to/from a dual graph.
    /// - Maintains conductivity and potential distributions synchronized with mesh state.
    /// </summary>
    public class FEMMesh : Discretization<FEMElement, FEMElectrode>
    {
        /// <summary>
        /// All vertices of the mesh. Vertex <c>GlobalId</c> values are used as keys across the mesh.
        /// </summary>
        public List<FEMVertex> Vertices { get; set; } = [];

        // All mesh edges discovered from element connectivity
        private readonly List<Edge> _edges = [];
        // Subset of _edges that lie on the outer boundary (referenced by exactly one element)
        private readonly List<Edge> _boundaryEdges = [];
        // Lookup from vertex id to vertex instance for fast access
        private Dictionary<int, FEMVertex> _vertexLookup = [];
        // Boundary vertices ordered along the boundary curve (counter-clockwise for a well-formed mesh)
        private List<FEMVertex> _orderedBoundaryVertices = [];
        // Mapping from vertex id to its index in _orderedBoundaryVertices
        private Dictionary<int, int> _boundaryOrderLookup = [];
        private bool _verticesAreSequentialFromZero;

        /// <summary>
        /// Creates a mesh from provided vertices/elements (and optional electrodes).
        /// Coordinates are normalized into [0,1]x[0,1] by default and topology is initialized.
        /// </summary>
        public FEMMesh(IEnumerable<FEMVertex> vertices,
                       IEnumerable<FEMElement> elements,
                       IEnumerable<FEMElectrode>? electrodes = null)
        {
            if (vertices != null)
                Vertices.AddRange(vertices);
            if (elements != null)
                _elements.AddRange(elements);
            if (electrodes != null)
                _electrodes.AddRange(electrodes);

            // Normalize to a stable coordinate scale to avoid extreme numeric ranges.
            NormalizeCoordinates();
            Initialize();
        }

        /// <summary>
        /// Creates an empty mesh structure and initializes internal structures.
        /// </summary>
        public FEMMesh()
        {
            Initialize();
        }

        /// <summary>
        /// Returns the list of mesh vertices (same as <see cref="Vertices"/>).
        /// </summary>
        public List<FEMVertex> GetVertices() => Vertices;

        /// <summary>
        /// Normalizes X and Y coordinates of all vertices into [minValue, maxValue].
        /// Degenerate axes (zero range) are collapsed to the midpoint of the target range.
        /// </summary>
        public void NormalizeCoordinates(double minValue = 0.0, double maxValue = 1.0)
        {
            if (Vertices.Count == 0)
                return;

            double targetRange = maxValue - minValue;
            if (targetRange <= 0.0)
                targetRange = 1.0;

            double minX = Vertices.Min(v => v.X);
            double maxX = Vertices.Max(v => v.X);
            double minY = Vertices.Min(v => v.Y);
            double maxY = Vertices.Max(v => v.Y);

            double rangeX = maxX - minX;
            double rangeY = maxY - minY;

            double midpoint = minValue + (targetRange * 0.5);

            foreach (var vertex in Vertices)
            {
                // Normalize X
                if (rangeX < 1e-12)
                {
                    vertex.X = midpoint;
                }
                else
                {
                    double normalizedX = (vertex.X - minX) / rangeX;
                    vertex.X = minValue + normalizedX * targetRange;
                }

                // Normalize Y
                if (rangeY < 1e-12)
                {
                    vertex.Y = midpoint;
                }
                else
                {
                    double normalizedY = (vertex.Y - minY) / rangeY;
                    vertex.Y = minValue + normalizedY * targetRange;
                }
            }
        }

        /// <summary>
        /// Initializes or refreshes derived state:
        /// - Creates a homogeneous conductivity distribution.
        /// - Builds the potential distribution from vertex potentials.
        /// - Rebuilds lookup dictionaries and edge/boundary topology.
        /// </summary>
        public void Initialize()
        {
            // Initialize with a homogeneous conductivity distribution
            ConductivityDistribution = ConductivityDistributionFactory.FromFEMMesh(this);

            Dictionary<int, double> potentialDistribution = new Dictionary<int, double>();

            foreach (var FEMVertex in Vertices)
                potentialDistribution.Add(FEMVertex.GlobalId, FEMVertex.Potential);

            PotentialDistribution = new PotentialDistribution(potentialDistribution);

            RebuildVertexLookup();
            BuildEdgeTopology();
        }

        /// <summary>
        /// Rebuilds the vertex id -> vertex instance dictionary.
        /// Call this after vertices change or are re-created.
        /// </summary>
        private void RebuildVertexLookup()
        {
            _vertexLookup = Vertices.ToDictionary(v => v.GlobalId);
            _verticesAreSequentialFromZero = true;
            for (int i = 0; i < Vertices.Count; i++)
            {
                if (Vertices[i].GlobalId != i)
                {
                    _verticesAreSequentialFromZero = false;
                    break;
                }
            }
        }

        /// <summary>
        /// Constructs the list of all edges from element connectivity and flags boundary edges
        /// (those referenced by exactly one element). Also derives the ordered boundary vertex list.
        /// </summary>
        private void BuildEdgeTopology()
        {
            _edges.Clear();
            _boundaryEdges.Clear();

            var edgeUsage = new Dictionary<(int a, int b), (Edge edge, int count)>();
            int edgeId = 0;

            // Count usage of each undirected edge across all triangles
            foreach (var element in _elements.Cast<FEMElement>())
            {
                var verts = element.Vertices;
                var triples = new (FEMVertex A, FEMVertex B)[]
                {
                    (verts[0], verts[1]),
                    (verts[1], verts[2]),
                    (verts[2], verts[0])
                };

                foreach (var (A, B) in triples)
                {
                    var key = (Math.Min(A.GlobalId, B.GlobalId), Math.Max(A.GlobalId, B.GlobalId));
                    if (edgeUsage.TryGetValue(key, out var entry))
                    {
                        entry.count++;
                        edgeUsage[key] = entry;
                    }
                    else
                    {
                        edgeUsage[key] = (new Edge(A, B, edgeId++), 1);
                    }
                }
            }

            // Boundary edges are those used by only one element
            foreach (var (_, value) in edgeUsage)
            {
                var edge = value.edge;
                edge.IsBoundary = value.count == 1;
                _edges.Add(edge);
                if (edge.IsBoundary)
                    _boundaryEdges.Add(edge);
            }

            BuildOrderedBoundaryVertices();
        }

        /// <summary>
        /// Builds a cyclic ordering of boundary vertices around the outer boundary.
        /// If topology is ambiguous, falls back to sorting by polar angle around the centroid.
        /// Populates <see cref="_orderedBoundaryVertices"/> and <see cref="_boundaryOrderLookup"/>.
        /// </summary>
        private void BuildOrderedBoundaryVertices()
        {
            _orderedBoundaryVertices = [];
            _boundaryOrderLookup = new Dictionary<int, int>();

            // Collect vertices marked as boundary
            var boundaryVerts = Vertices.Where(v => v.IsBoundary).ToList();
            if (boundaryVerts.Count == 0)
                return;

            // Build boundary adjacency from boundary edges
            var adjacency = new Dictionary<int, List<int>>();
            foreach (var edge in _boundaryEdges)
            {
                int a = edge.Start.GlobalId;
                int b = edge.End.GlobalId;
                if (!adjacency.TryGetValue(a, out var listA))
                {
                    listA = new List<int>();
                    adjacency[a] = listA;
                }
                if (!listA.Contains(b))
                    listA.Add(b);

                if (!adjacency.TryGetValue(b, out var listB))
                {
                    listB = new List<int>();
                    adjacency[b] = listB;
                }
                if (!listB.Contains(a))
                    listB.Add(a);
            }

            List<FEMVertex> ordered;
            if (adjacency.Count == 0)
            {
                // No adjacency information: order by angle around the centroid
                double cx = boundaryVerts.Average(v => v.X);
                double cy = boundaryVerts.Average(v => v.Y);
                ordered = [.. boundaryVerts.OrderBy(v => Math.Atan2(v.Y - cy, v.X - cx))];
            }
            else
            {
                // Walk the boundary using adjacency starting from the lowest-angle boundary vertex
                double cx = boundaryVerts.Average(v => v.X);
                double cy = boundaryVerts.Average(v => v.Y);
                var start = boundaryVerts
                    .OrderBy(v => Math.Atan2(v.Y - cy, v.X - cx))
                    .First();

                ordered = new List<FEMVertex>(boundaryVerts.Count);
                int current = start.GlobalId;
                int previous = -1;
                var visited = new HashSet<int>();

                while (visited.Add(current))
                {
                    if (!_vertexLookup.TryGetValue(current, out var vertex))
                        break;

                    ordered.Add(vertex);

                    if (!adjacency.TryGetValue(current, out var neighbours) || neighbours.Count == 0)
                        break;

                    int next = -1;
                    if (neighbours.Count == 1)
                    {
                        next = neighbours[0];
                    }
                    else
                    {
                        // Prefer the neighbor that is not the previous vertex to maintain direction
                        foreach (var candidate in neighbours)
                        {
                            if (candidate != previous)
                            {
                                next = candidate;
                                break;
                            }
                        }
                        if (next == -1)
                            next = neighbours[0];
                    }

                    previous = current;
                    current = next;

                    // Stop if we came full circle
                    if (current == start.GlobalId)
                        break;
                }

                // If we failed to collect all boundary vertices, fall back to angle sort
                if (ordered.Count != boundaryVerts.Count)
                {
                    double cx2 = boundaryVerts.Average(v => v.X);
                    double cy2 = boundaryVerts.Average(v => v.Y);
                    ordered = [.. boundaryVerts.OrderBy(v => Math.Atan2(v.Y - cy2, v.X - cx2))];
                }
            }

            _orderedBoundaryVertices = ordered;
            for (int i = 0; i < _orderedBoundaryVertices.Count; i++)
                _boundaryOrderLookup[_orderedBoundaryVertices[i].GlobalId] = i;
        }

        /// <summary>
        /// Keys of the current state space for potential distribution (vertex ids).
        /// </summary>
        protected override IEnumerable<int> StateKeys() => Vertices.Select(v => v.GlobalId);

        /// <summary>
        /// Writes a single vertex potential back to the mesh state.
        /// </summary>
        protected override void ApplyPotentialToState(int stateKey, double potential)
        {
            var v = GetVertexById(stateKey);
            v.Potential = potential;
        }

        /// <summary>
        /// Reads an electrode potential from the state. For patch electrodes, averages over referenced vertices.
        /// For point electrodes, uses the representative <see cref="FEMElectrode.MeshId"/> vertex.
        /// </summary>
        protected override double ReadPotentialOf(FEMElectrode e)
        {
            if (!e.PointElectrode && e.FEMVertexIds != null && e.FEMVertexIds.Count > 0)
            {
                double sum = 0.0;
                int count = 0;
                foreach (int id in e.FEMVertexIds)
                {
                    sum += GetVertexById(id).Potential;
                    count++;
                }

                return count > 0 ? sum / count : 0.0;
            }

            var vv = GetVertexById(e.MeshId);
            return vv.Potential;
        }

        public void ApplySolvedPotentialDistribution(PotentialDistribution distribution)
        {
            if (distribution == null)
                throw new ArgumentNullException(nameof(distribution));

            PotentialDistribution = distribution;

            if (distribution.TryGetDenseStorage(out var densePotentials, out var densePotentialsCompact, out int denseMinKey))
            {
                if (densePotentials != null)
                    ApplyDensePotentials(densePotentials, denseMinKey);
                else if (densePotentialsCompact != null)
                    ApplyDensePotentials(densePotentialsCompact, denseMinKey);
            }
            else
            {
                foreach (var kvp in distribution.Potentials)
                    ApplyPotentialToState(kvp.Key, kvp.Value);
            }

            RefreshElectrodePotentialsFromState();
        }

        private void ApplyDensePotentials(double[] potentials, int denseMinKey)
        {
            if (_verticesAreSequentialFromZero && denseMinKey == 0 && potentials.Length == Vertices.Count)
            {
                for (int i = 0; i < potentials.Length; i++)
                    Vertices[i].Potential = potentials[i];
                return;
            }

            for (int i = 0; i < potentials.Length; i++)
                GetVertexById(denseMinKey + i).Potential = potentials[i];
        }

        private void ApplyDensePotentials(float[] potentials, int denseMinKey)
        {
            if (_verticesAreSequentialFromZero && denseMinKey == 0 && potentials.Length == Vertices.Count)
            {
                for (int i = 0; i < potentials.Length; i++)
                    Vertices[i].Potential = potentials[i];
                return;
            }

            for (int i = 0; i < potentials.Length; i++)
                GetVertexById(denseMinKey + i).Potential = potentials[i];
        }

        /// <summary>
        /// Places <paramref name="numElectrodes"/> electrodes approximately evenly along the outer boundary.
        /// Each electrode spans <paramref name="nodesPerElectrode"/> consecutive boundary nodes (>=1).
        /// Sets z-contact and approximates length from geometry (falls back to <paramref name="lengthHint"/>).
        /// </summary>
        public void PlaceEquidistantElectrodes(int numElectrodes, double zContact, double lengthHint, int nodesPerElectrode = 1, VirtualElectrodeSettings? virtualElectrodeSettings = null)
        {
            // Clear previous electrode flags on vertices
            foreach (var v in Vertices)
            {
                v.IsElectrode = false;
                v.ElectrodeId = -1;
            }

            if (numElectrodes <= 0)
            {
                SetElectrodes(new List<FEMElectrode>());
                return;
            }

            if (_orderedBoundaryVertices.Count == 0)
                BuildEdgeTopology();

            var boundary = _orderedBoundaryVertices;
            if (boundary.Count == 0)
            {
                SetElectrodes(new List<FEMElectrode>());
                return;
            }

            // Clamp requested counts to boundary size
            numElectrodes = Math.Clamp(numElectrodes, 1, boundary.Count);
            nodesPerElectrode = Math.Clamp(nodesPerElectrode, 1, Math.Max(1, boundary.Count / numElectrodes));

            var used = new bool[boundary.Count];
            var electrodes = new List<FEMElectrode>(numElectrodes);
            double step = boundary.Count / (double)numElectrodes; // fractional step along the ring
            double pos = 0.0;

            for (int i = 0; i < numElectrodes; i++)
            {
                // Find nearest unused boundary index to the target position
                int startIndex = FindNextUnusedIndex(used, (int)Math.Round(pos) % boundary.Count);
                if (startIndex < 0)
                    break;

                var assigned = new List<int>(nodesPerElectrode);
                int current = startIndex;
                // Assign consecutive boundary nodes to this electrode
                for (int n = 0; n < nodesPerElectrode; n++)
                {
                    if (used[current])
                    {
                        current = FindNextUnusedIndex(used, current);
                        if (current < 0)
                            break;
                    }

                    used[current] = true;
                    var vertex = boundary[current];
                    vertex.IsElectrode = true;
                    vertex.ElectrodeId = i;
                    assigned.Add(vertex.GlobalId);

                    current = (current + 1) % boundary.Count;
                }

                if (assigned.Count == 0)
                    continue;

                // Create electrode; patch vs point decided by assigned count
                var electrode = new FEMElectrode(
                    id: i,
                    meshId: assigned[0],
                    current: 0.0,
                    zContact: zContact,
                    voltage: 0.0,
                    pointElectrode: assigned.Count == 1,
                    isVirtual: false);
                electrode.PointElectrode = assigned.Count == 1;
                electrode.FEMVertexIds.AddRange(assigned);
                double arcLength = ComputeElectrodeLength(assigned);
                electrode.Length = arcLength > 0.0 ? arcLength : lengthHint;
                electrodes.Add(electrode);

                pos += step; // advance to next target position
        }

        // Clear any remaining vertex electrode flags that were not assigned
        for (int idx = 0; idx < boundary.Count; idx++)
        {
                if (!used[idx])
                {
                    boundary[idx].IsElectrode = false;
                    boundary[idx].ElectrodeId = -1;
                }
            }

            SetElectrodes(electrodes);

            if (virtualElectrodeSettings != null)
                ApplyVirtualElectrodes(virtualElectrodeSettings, zContact);
        }

        public void ApplyVirtualElectrodes(VirtualElectrodeSettings settings, double defaultZContact)
        {
            if (settings == null)
                throw new ArgumentNullException(nameof(settings));

            if (_electrodes.Count > 0)
            {
                var idLookup = _electrodes.ToDictionary(e => e.Id);
                foreach (var vertex in Vertices)
                {
                    if (!vertex.IsElectrode || vertex.ElectrodeId < 0)
                        continue;

                    if (idLookup.TryGetValue(vertex.ElectrodeId, out var electrode) && electrode.IsVirtual)
                    {
                        vertex.IsElectrode = false;
                        vertex.ElectrodeId = -1;
                    }
                }

                var realElectrodes = _electrodes.Where(e => !e.IsVirtual).OrderBy(e => e.Id).Cast<FEMElectrode>().ToList();
                SetElectrodes(realElectrodes);
            }

            if (!settings.ShouldApplyVirtualElectrodes() || settings.VirtualElectrodesPerGap <= 0 || _electrodes.Count < 2)
                return;

            if (_orderedBoundaryVertices.Count == 0)
                BuildEdgeTopology();

            var boundary = _orderedBoundaryVertices;
            if (boundary.Count == 0)
                return;

            int perGap = Math.Max(1, settings.VirtualElectrodesPerGap);
            double cx = boundary.Average(v => v.X);
            double cy = boundary.Average(v => v.Y);
            var boundaryAngles = new double[boundary.Count];
            for (int i = 0; i < boundary.Count; i++)
                boundaryAngles[i] = NormalizeAngle(Math.Atan2(boundary[i].Y - cy, boundary[i].X - cx));

            var usedBoundaryIndices = new bool[boundary.Count];
            var orderedReal = _electrodes.Cast<FEMElectrode>()
                .Select(e => (Electrode: e, Angle: ComputeElectrodeAngle(e, cx, cy)))
                .OrderBy(entry => entry.Angle)
                .ToList();

            foreach (var (electrode, _) in orderedReal)
            {
                foreach (int vid in GetElectrodeVertexIds(electrode))
                {
                    if (_boundaryOrderLookup.TryGetValue(vid, out int boundaryIndex))
                    {
                        usedBoundaryIndices[boundaryIndex] = true;
                        var vertex = boundary[boundaryIndex];
                        vertex.IsElectrode = true;
                        vertex.ElectrodeId = electrode.Id;
                    }
                }
            }

            var augmented = new List<FEMElectrode>(_electrodes.Cast<FEMElectrode>());
            int nextId = augmented.Count;

            for (int i = 0; i < orderedReal.Count; i++)
            {
                var current = orderedReal[i];
                var next = orderedReal[(i + 1) % orderedReal.Count];
                double span = AngleDelta(current.Angle, next.Angle);

                double leftZ = current.Electrode.ZContact;
                double rightZ = next.Electrode.ZContact;
                double zContact = (double.IsFinite(leftZ) && double.IsFinite(rightZ))
                    ? 0.5 * (leftZ + rightZ)
                    : defaultZContact;

                for (int k = 0; k < perGap; k++)
                {
                    double fraction = (k + 1.0) / (perGap + 1.0);
                    double targetAngle = NormalizeAngle(current.Angle + span * fraction);
                    int boundaryIndex = FindClosestAvailableBoundaryVertex(targetAngle, boundaryAngles, usedBoundaryIndices);
                    if (boundaryIndex < 0)
                        continue;

                    var vertex = boundary[boundaryIndex];
                    usedBoundaryIndices[boundaryIndex] = true;

                    var virtualElectrode = new FEMElectrode(
                        id: nextId,
                        meshId: vertex.GlobalId,
                        current: 0.0,
                        zContact: zContact,
                        voltage: 0.0,
                        isExcitation: false,
                        isGround: false,
                        isMeasuring: true,
                        pointElectrode: true,
                        isVirtual: true)
                    {
                        PointElectrode = true
                    };
                    virtualElectrode.FEMVertexIds.Add(vertex.GlobalId);
                    virtualElectrode.Length = ComputeElectrodeLength(new List<int> { vertex.GlobalId });

                    vertex.IsElectrode = true;
                    vertex.ElectrodeId = nextId;

                    augmented.Add(virtualElectrode);
                    nextId++;
                }
            }

            SetElectrodes(augmented);
        }

        public Dictionary<int, double> GetElectrodeAngles()
        {
            if (_orderedBoundaryVertices.Count == 0)
                BuildEdgeTopology();

            var boundary = _orderedBoundaryVertices;
            double cx = boundary.Count > 0 ? boundary.Average(v => v.X) : 0.0;
            double cy = boundary.Count > 0 ? boundary.Average(v => v.Y) : 0.0;
            var angles = new Dictionary<int, double>(_electrodes.Count);

            foreach (var electrode in _electrodes.Cast<FEMElectrode>())
                angles[electrode.Id] = ComputeElectrodeAngle(electrode, cx, cy);

            return angles;
        }

        private static double NormalizeAngle(double angle)
        {
            double twoPi = Math.PI * 2.0;
            double result = angle % twoPi;
            if (result < 0)
                result += twoPi;
            return result;
        }

        private static double AngleDelta(double from, double to)
        {
            double delta = NormalizeAngle(to - from);
            if (delta <= 0)
                delta += Math.PI * 2.0;
            return delta;
        }

        private double ComputeElectrodeAngle(FEMElectrode electrode, double cx, double cy)
        {
            var ids = GetElectrodeVertexIds(electrode).ToList();
            if (ids.Count == 0)
                return 0.0;

            double avgX = 0.0;
            double avgY = 0.0;
            foreach (int vid in ids)
            {
                var vertex = GetVertexById(vid);
                avgX += vertex.X;
                avgY += vertex.Y;
            }
            avgX /= ids.Count;
            avgY /= ids.Count;
            return NormalizeAngle(Math.Atan2(avgY - cy, avgX - cx));
        }

        private IEnumerable<int> GetElectrodeVertexIds(FEMElectrode electrode)
        {
            if (electrode.FEMVertexIds != null && electrode.FEMVertexIds.Count > 0)
                return electrode.FEMVertexIds;
            if (electrode.MeshId >= 0)
                return new[] { electrode.MeshId };
            return Array.Empty<int>();
        }

        private static int FindClosestAvailableBoundaryVertex(double targetAngle, double[] boundaryAngles, bool[] used)
        {
            int bestIndex = -1;
            double bestScore = double.MaxValue;
            double twoPi = Math.PI * 2.0;

            for (int i = 0; i < boundaryAngles.Length; i++)
            {
                if (used[i])
                    continue;

                double diff = Math.Abs(boundaryAngles[i] - targetAngle);
                diff = Math.Min(diff, twoPi - diff);
                if (diff < bestScore)
                {
                    bestScore = diff;
                    bestIndex = i;
                }
            }

            return bestIndex;
        }

        /// <summary>
        /// Returns the boundary vertices ordered along the outer boundary curve.
        /// </summary>
        public IReadOnlyList<FEMVertex> GetOrderedBoundaryVertices() => _orderedBoundaryVertices;

        /// <summary>
        /// Gets the boundary-order index (0..N-1) of a boundary vertex id.
        /// </summary>
        public bool TryGetBoundaryIndex(int vertexId, out int index) => _boundaryOrderLookup.TryGetValue(vertexId, out index);

        /// <summary>
        /// Returns a vertex by its global id or throws if not present.
        /// </summary>
        public FEMVertex GetVertexById(int vertexId)
            => _vertexLookup.TryGetValue(vertexId, out var vertex)
                ? vertex
                : throw new InvalidOperationException($"No FEMVertex.GlobalId = {vertexId}.");

        /// <summary>
        /// Orders the specified vertex ids according to their position along the boundary order.
        /// If ids are not contiguous, the sequence is rotated to minimize wrap-around (largest gap is cut).
        /// Non-boundary ids are ignored.
        /// </summary>
        public List<int> OrderVerticesAlongBoundary(IEnumerable<int> vertexIds)
        {
            if (vertexIds == null)
                return [];

            var ids = vertexIds.Where(id => _boundaryOrderLookup.ContainsKey(id)).Distinct().ToList();
            if (ids.Count <= 1)
                return ids;

            // First collect in boundary order
            var ordered = new List<int>(ids.Count);
            var idSet = new HashSet<int>(ids);

            foreach (var vertex in _orderedBoundaryVertices)
            {
                if (idSet.Contains(vertex.GlobalId))
                    ordered.Add(vertex.GlobalId);
            }

            if (ordered.Count <= 1)
                return ordered;

            // Rotate so that the largest boundary gap is between the last and first element
            int n = ordered.Count;
            int boundaryCount = _orderedBoundaryVertices.Count;
            var boundaryIndices = ordered.Select(id => _boundaryOrderLookup[id]).ToArray();
            int bestBreak = 0;
            int maxGap = -1;

            for (int i = 0; i < n; i++)
            {
                int current = boundaryIndices[i];
                int next = boundaryIndices[(i + 1) % n];
                int diff = (next - current + boundaryCount) % boundaryCount;
                if (diff > maxGap)
                {
                    maxGap = diff;
                    bestBreak = (i + 1) % n;
                }
            }

            if (bestBreak == 0)
                return ordered;

            var rotated = new List<int>(n);
            for (int offset = 0; offset < n; offset++)
                rotated.Add(ordered[(bestBreak + offset) % n]);
            return rotated;
        }

        /// <summary>
        /// Computes the arc length of a boundary electrode given an ordered list of boundary vertex ids.
        /// - For a single vertex, returns half of the sum of adjacent boundary edge lengths.
        /// - For multiple vertices, sums pairwise distances along the provided order.
        /// </summary>
        public double ComputeElectrodeLength(IList<int> orderedVertexIds)
        {
            if (orderedVertexIds == null || orderedVertexIds.Count == 0)
                return 0.0;

            if (orderedVertexIds.Count == 1)
            {
                // Use half of neighbor edge lengths to approximate local contact width
                if (_orderedBoundaryVertices.Count < 2)
                    return 0.0;
                if (!_boundaryOrderLookup.TryGetValue(orderedVertexIds[0], out var idx))
                    return 0.0;
                int prevIdx = (idx - 1 + _orderedBoundaryVertices.Count) % _orderedBoundaryVertices.Count;
                int nextIdx = (idx + 1) % _orderedBoundaryVertices.Count;
                var current = GetVertexById(orderedVertexIds[0]);
                var prev = _orderedBoundaryVertices[prevIdx];
                var next = _orderedBoundaryVertices[nextIdx];
                return Distance(current, prev) * 0.5 + Distance(current, next) * 0.5;
            }

            double length = 0.0;
            for (int i = 0; i < orderedVertexIds.Count - 1; i++)
            {
                var a = GetVertexById(orderedVertexIds[i]);
                var b = GetVertexById(orderedVertexIds[i + 1]);
                length += Distance(a, b);
            }
            return length;
        }

        /// <summary>
        /// Recomputes and assigns the physical surface length of every electrode from the current mesh geometry.
        /// Falls back to average boundary spacing and then a small positive guard if no geometry is available.
        /// </summary>
        public void UpdateElectrodeLengths()
        {
            if (_electrodes.Count == 0)
                return;

            if (_orderedBoundaryVertices.Count == 0)
                BuildEdgeTopology();

            double averageBoundarySpacing = EstimateAverageBoundarySpacing();

            foreach (var electrode in ElectrodesTyped.Cast<FEMElectrode>())
            {
                double length = 0.0;
                List<int>? contactVertexIds = null;

                if (electrode.FEMVertexIds != null && electrode.FEMVertexIds.Count > 0)
                {
                    // Reorder vertices so the arc length follows the boundary instead
                    // of the arbitrary order that may have been stored in the file.
                    contactVertexIds = OrderVerticesAlongBoundary(electrode.FEMVertexIds);
                }
                else if (electrode.MeshId >= 0)
                {
                    // The electrode references a single representative node.
                    contactVertexIds = new List<int> { electrode.MeshId };
                }

                if (contactVertexIds != null && contactVertexIds.Count > 0)
                    length = ComputeElectrodeLength(contactVertexIds);

                if (length <= 0.0 || double.IsNaN(length))
                    length = averageBoundarySpacing;

                if (length <= 0.0 || double.IsNaN(length))
                    length = 1e-6;

                electrode.Length = length;
            }
        }

        /// <summary>
        /// Estimates the average spacing of boundary vertices along the ordered boundary loop.
        /// </summary>
        private double EstimateAverageBoundarySpacing()
        {
            int count = _orderedBoundaryVertices.Count;
            if (count < 2)
                return 0.0;

            double total = 0.0;
            for (int i = 0; i < count; i++)
            {
                var a = _orderedBoundaryVertices[i];
                var b = _orderedBoundaryVertices[(i + 1) % count];
                total += Distance(a, b);
            }

            return total / count;
        }

        /// <summary>
        /// Enumerates contiguous boundary line segments for each patch electrode (2 or more vertices).
        /// Useful for visualization or length verification.
        /// </summary>
        public IEnumerable<(FEMVertex Start, FEMVertex End, FEMElectrode Electrode)> GetElectrodeSegments()
        {
            foreach (var electrode in ElectrodesTyped.Cast<FEMElectrode>())
            {
                if (electrode.FEMVertexIds == null || electrode.FEMVertexIds.Count < 2)
                    continue;

                var ids = electrode.FEMVertexIds;
                for (int i = 0; i < ids.Count - 1; i++)
                {
                    if (_vertexLookup.TryGetValue(ids[i], out var start) &&
                        _vertexLookup.TryGetValue(ids[i + 1], out var end))
                    {
                        yield return (start, end, electrode);
                    }
                }
            }
        }

        /// <summary>
        /// Scans the circular boolean array starting at <paramref name="start"/> and returns the first index that is false.
        /// Returns -1 if all entries are used.
        /// </summary>
        private static int FindNextUnusedIndex(bool[] used, int start)
        {
            int n = used.Length;
            for (int step = 0; step < n; step++)
            {
                int idx = (start + step) % n;
                if (!used[idx])
                    return idx;
            }
            return -1;
        }

        /// <summary>
        /// Euclidean distance between two vertices.
        /// </summary>
        private static double Distance(FEMVertex a, FEMVertex b)
        {
            double dx = a.X - b.X;
            double dy = a.Y - b.Y;
            return Math.Sqrt(dx * dx + dy * dy);
        }


        /// <summary>
        /// Creates a deep copy of this FEMMesh, including vertices, elements,
        /// electrode list, and distributions. Electrode lengths are re-derived on the copy.
        /// </summary>
        public override Discretization DeepCopy()
        {
            var FEMVertexMap = new Dictionary<int, FEMVertex>(Vertices.Count);
            var newVertices = new List<FEMVertex>(Vertices.Count);

            // Copy vertices
            foreach (var v in Vertices)
            {
                var v2 = new FEMVertex
                {
                    GlobalId = v.GlobalId,
                    BoundaryId = v.BoundaryId,
                    ElectrodeId = v.ElectrodeId,
                    X = v.X,
                    Y = v.Y,
                    IsBoundary = v.IsBoundary,
                    IsElectrode = v.IsElectrode,
                    Potential = v.Potential
                };
                FEMVertexMap[v.GlobalId] = v2;
                newVertices.Add(v2);
            }

            // Copy elements and rebind to new vertex instances
            var newElements = new List<FEMElement>(_elements.Count);
            foreach (var el in _elements)
            {
                var a = FEMVertexMap[el.Vertices[0].GlobalId];
                var b = FEMVertexMap[el.Vertices[1].GlobalId];
                var c = FEMVertexMap[el.Vertices[2].GlobalId];

                var el2 = new FEMElement(el.Id, a, b, c)
                {
                    Conductivity = el.Conductivity
                };
                newElements.Add(el2);
            }

            var copy = new FEMMesh(newVertices, newElements)
            {
                Metadata = new DiscretizationMetaData
                {
                    CreatedOn = this.Metadata.CreatedOn,
                    Generator = this.Metadata.Generator,
                    Parameters = new Dictionary<string, string>(this.Metadata.Parameters)
                }
            };

            // Copy electrodes (preserving ids and patch membership); then re-derive lengths
            if (_electrodes.Count > 0)
            {
                var electrodeCopies = new List<FEMElectrode>(_electrodes.Count);
                foreach (var e in _electrodes)
                {
                    var e2 = new FEMElectrode(
                        id: e.Id,
                        meshId: e.MeshId,
                        current: e.Current,
                        zContact: e.ZContact,
                        voltage: e.Potential,
                        isExcitation: e.IsExcitation,
                        isGround: e.IsGround,
                        isMeasuring: e.IsMeasuring,
                        pointElectrode: e.PointElectrode
                    );
                    e2.Length = e.Length;
                    if (e.FEMVertexIds?.Count > 0)
                        e2.FEMVertexIds.AddRange(e.FEMVertexIds);

                    electrodeCopies.Add(e2);
                }
                copy.SetElectrodes(electrodeCopies);
                copy.UpdateElectrodeLengths();
            }
            else
            {
                copy.SetElectrodes(new List<FEMElectrode>());
            }

            // Copy scalar fields
            copy.SetConductivityDistribution(new ConductivityDistribution(new Dictionary<int, double>(this.ConductivityDistribution.Conductivities)));
            copy.SetPotentialDistribution(new PotentialDistribution(new Dictionary<int, double>(this.PotentialDistribution.Potentials)));
            return copy;
        }

        /// <summary>
        /// Logs a brief summary of the mesh size to the console.
        /// </summary>
        public override void LogDiscretization()
        {
            Console.WriteLine($"FEM | V={Vertices.Count}, E={_elements.Count}, EL={_electrodes.Count}");
        }

        /// <summary>
        /// Uniformly refines the mesh by splitting every triangle into four, <paramref name="levels"/> times.
        /// </summary>
        public override FEMMesh RefineUniform(int levels = 1)
        {
            var current = (FEMMesh)this.DeepCopy();
            for (int L = 0; L < Math.Max(1, levels); L++)
                current = current.RefineOnce();
            return current;
        }

        /// <summary>
        /// Performs a single uniform refinement pass (4 children per triangle).
        /// Midpoints inherit averaged position/potential; boundary status is preserved on boundary edges.
        /// Electrodes are remapped to nearest vertex in the refined mesh.
        /// </summary>
        private FEMMesh RefineOnce()
        {
            var oldVerts = this.Vertices;
            var oldElems = this.ElementsTyped.Cast<FEMElement>().ToList();

            // Midpoint cache per edge (min,max) -> FEMVertex
            var mpt = new Dictionary<(int, int), FEMVertex>();
            var newVerts = new List<FEMVertex>(oldVerts.Count * 2);

            // Copy old vertices first
            var idMap = new Dictionary<int, FEMVertex>(oldVerts.Count);
            foreach (var v in oldVerts)
            {
                var nv = new FEMVertex
                {
                    GlobalId = v.GlobalId,
                    X = v.X,
                    Y = v.Y,
                    Potential = v.Potential,
                    IsBoundary = v.IsBoundary,
                    BoundaryId = v.BoundaryId,
                    ElectrodeId = v.ElectrodeId,
                    IsElectrode = v.IsElectrode
                };
                newVerts.Add(nv);
                idMap[v.GlobalId] = nv;
            }

            // Helper to get/add midpoint FEMVertex
            FEMVertex Midpoint(FEMVertex a, FEMVertex b)
            {
                int ia = a.GlobalId, ib = b.GlobalId;
                var key = (Math.Min(ia, ib), Math.Max(ia, ib));
                if (mpt.TryGetValue(key, out var mv)) return mv;

                var m = new FEMVertex
                {
                    GlobalId = newVerts.Count,
                    X = 0.5 * (a.X + b.X),
                    Y = 0.5 * (a.Y + b.Y),
                    Potential = 0.5 * (a.Potential + b.Potential),
                    IsBoundary = a.IsBoundary && b.IsBoundary, // edge on boundary => midpoint is boundary
                    BoundaryId = (a.IsBoundary && b.IsBoundary) ? Math.Max(a.BoundaryId, b.BoundaryId) : 0
                };
                newVerts.Add(m);
                mpt[key] = m;
                return m;
            }

            var newElems = new List<FEMElement>(oldElems.Count * 4);
            int eid = 0;
            foreach (var el in oldElems)
            {
                var a = idMap[el.Vertices[0].GlobalId];
                var b = idMap[el.Vertices[1].GlobalId];
                var c = idMap[el.Vertices[2].GlobalId];

                var mAB = Midpoint(a, b);
                var mBC = Midpoint(b, c);
                var mCA = Midpoint(c, a);

                // 4 children
                var e1 = new FEMElement(eid++, a, mAB, mCA) { Conductivity = el.Conductivity };
                var e2 = new FEMElement(eid++, b, mBC, mAB) { Conductivity = el.Conductivity };
                var e3 = new FEMElement(eid++, c, mCA, mBC) { Conductivity = el.Conductivity };
                var e4 = new FEMElement(eid++, mAB, mBC, mCA) { Conductivity = el.Conductivity };

                newElems.AddRange(new[] { e1, e2, e3, e4 });
            }

            var refined = new FEMMesh(newVerts, newElems);

            // Re-map electrodes to nearest FEMVertex by position
            if (this.ElectrodesTyped.Count > 0)
            {
                var newEls = new List<FEMElectrode>(this.ElectrodesTyped.Count);
                foreach (var e in this.ElectrodesTyped)
                {
                    var oldV = this.Vertices.FirstOrDefault(v => v.GlobalId == e.MeshId);
                    if (oldV == null) continue;

                    int best = -1;
                    double bestD2 = double.MaxValue;
                    for (int i = 0; i < refined.Vertices.Count; i++)
                    {
                        var v = refined.Vertices[i];
                        double dx = v.X - oldV.X, dy = v.Y - oldV.Y, d2 = dx * dx + dy * dy;
                        if (d2 < bestD2) { bestD2 = d2; best = v.GlobalId; }
                    }

                    var e2 = new FEMElectrode(
                        id: e.Id,
                        meshId: best,
                        current: e.Current,
                        zContact: e.ZContact,
                        voltage: e.Potential,
                        isExcitation: e.IsExcitation,
                        isGround: e.IsGround,
                        isMeasuring: e.IsMeasuring,
                        pointElectrode: e.PointElectrode
                    );
                    if (e.FEMVertexIds?.Count > 0) e2.FEMVertexIds.AddRange(e.FEMVertexIds);
                    newEls.Add(e2);
                }
                refined.SetElectrodes(newEls);
                refined.UpdateElectrodeLengths();
            }

            // Refresh distributions on the refined mesh
            var cd = refined.ElementsTyped.Cast<FEMElement>().ToDictionary(e => e.Id, e => e.Conductivity);
            refined.SetConductivityDistribution(new ConductivityDistribution(cd));

            var pd = refined.Vertices.ToDictionary(v => v.GlobalId, v => v.Potential);
            refined.SetPotentialDistribution(new PotentialDistribution(pd));

            return refined;
        }


        /// <summary>
        /// Builds the dual graph of the mesh:
        /// - One graph vertex per FEM element (located at the centroid).
        /// - Graph edges connect elements that share a mesh edge.
        /// - Edge weights are two-point transmissibilities based on face length and centroid distances.
        /// </summary>
        /// <returns>The graph object created from the FEM discretization.</returns>
        public override Utility.Classes.Discretizer.GraphMesh.Graph ToGraph()
        {
            // -- Build dual graph: one GraphFEMVertex per FEM element (at its centroid),
            //    connect elements that share an edge, with two-point flux/transmissibility weight.

            // Local helpers
            static (double x, double y) Centroid(FEMElement el)
            {
                var v0 = el.Vertices[0]; var v1 = el.Vertices[1]; var v2 = el.Vertices[2];
                return ((v0.X + v1.X + v2.X) / 3.0, (v0.Y + v1.Y + v2.Y) / 3.0);
            }

            static double EdgeLength(FEMVertex a, FEMVertex b)
            {
                double dx = a.X - b.X, dy = a.Y - b.Y;
                return Math.Sqrt(dx * dx + dy * dy);
            }

            // distance from point P to the infinite line through A-B (2D)
            static double DistancePointToLine((double x, double y) P, FEMVertex A, FEMVertex B)
            {
                double ux = B.X - A.X, uy = B.Y - A.Y;
                double vx = P.x - A.X, vy = P.y - A.Y;
                double cross = Math.Abs(ux * vy - uy * vx);
                double normU = Math.Sqrt(ux * ux + uy * uy);
                if (normU <= 1e-15) return 0.0;
                return cross / normU;
            }

            var elements = _elements.Cast<FEMElement>().ToList();
            int ne = elements.Count;

            // 1) Build vertices: one per element (centroid), mark boundary if element has a boundary edge
            //    First collect which mesh edges are boundary: an edge that belongs to exactly one element.
            var edgeToElements = new Dictionary<(int a, int b), List<int>>(); // undirected key by sorted FEMVertex ids

            for (int ei = 0; ei < ne; ei++)
            {
                var el = elements[ei];
                var v = el.Vertices;
                var triples = new (FEMVertex A, FEMVertex B)[] { (v[0], v[1]), (v[1], v[2]), (v[2], v[0]) };
                foreach (var (A, B) in triples)
                {
                    var key = (Math.Min(A.GlobalId, B.GlobalId), Math.Max(A.GlobalId, B.GlobalId));
                    if (!edgeToElements.TryGetValue(key, out var list))
                    {
                        list = new List<int>(2);
                        edgeToElements[key] = list;
                    }
                    list.Add(ei);
                }
            }

            // Determine boundary edges (edge that belongs to exactly one element)
            var isBoundaryElement = new bool[ne];
            foreach (var kvp in edgeToElements)
            {
                var els = kvp.Value;
                if (els.Count == 1)
                {
                    isBoundaryElement[els[0]] = true; // the lone owner is a boundary element
                }
            }

            // Create GraphFEMVertex list
            var gVertices = new List<Utility.Classes.Discretizer.GraphMesh.GraphFEMVertex>(ne);
            for (int ei = 0; ei < ne; ei++)
            {
                var el = elements[ei];
                var (cx, cy) = Centroid(el);
                int domainId = 0;               
                int boundaryId = isBoundaryElement[ei] ? 1 : 0;
                // Use element Id for GlobalId so it’s easy to map back
                gVertices.Add(new Utility.Classes.Discretizer.GraphMesh.GraphFEMVertex(cx, cy, el.Id, domainId, boundaryId));
            }

            // 2) Build edges between neighboring elements that share a mesh edge,
            //    weight = |Γ| / (d_i/σ_i + d_j/σ_j) (two-point flux)
            var idToIndex = elements.ToDictionary(e => e.Id, e => elements.IndexOf(e));
            var gEdges = new List<Utility.Classes.Discretizer.GraphMesh.GraphEdge>();

            foreach (var kvp in edgeToElements)
            {
                var els = kvp.Value;
                if (els.Count != 2) continue; // interior face must be shared by exactly 2 elements

                int i = els[0];
                int j = els[1];
                var Ei = elements[i];
                var Ej = elements[j];

                // The common (mesh) edge endpoints (recover vertices by matching sorted ids)
                int aId = kvp.Key.a;
                int bId = kvp.Key.b;

                FEMVertex Ai = Ei.Vertices.First(v => v.GlobalId == aId || v.GlobalId == bId);
                FEMVertex Bi = Ei.Vertices.First(v => (v.GlobalId == aId || v.GlobalId == bId) && !ReferenceEquals(v, Ai));
                // (Ai,Bi) is the common edge (in practice Ai!=Bi and same A/B for Ej)

                double faceLen = EdgeLength(Ai, Bi);
                if (faceLen <= 1e-15) continue;

                // centroids
                var Ci = Centroid(Ei);
                var Cj = Centroid(Ej);

                // distances to the face line
                double di = DistancePointToLine(Ci, Ai, Bi);
                double dj = DistancePointToLine(Cj, Ai, Bi);

                // conductivities (piecewise-constant per element)
                double sigi = (Ei.Conductivity > 0.0) ? Ei.Conductivity : 1.0;
                double sigj = (Ej.Conductivity > 0.0) ? Ej.Conductivity : 1.0;

                // two-point transmissibility
                double denom = di / Math.Max(sigi, 1e-15) + dj / Math.Max(sigj, 1e-15);
                if (denom <= 1e-15) continue;
                double tau = faceLen / denom;

                // Graph vertices refer to element-ids, fetch those
                var Gi = gVertices[i];
                var Gj = gVertices[j];
                gEdges.Add(new Utility.Classes.Discretizer.GraphMesh.GraphEdge(Gi, Gj, tau));
            }

            return new Utility.Classes.Discretizer.GraphMesh.Graph(gVertices, gEdges);
        }

        /// <summary>
        /// Reconstructs a FEM mesh from a dual graph produced by <see cref="ToGraph"/>.
        /// - Creates a vertex for each graph vertex using its coordinates.
        /// - Finds triangles as 3-cliques in the graph adjacency.
        /// - Sets element conductivity as the average of the three edge weights.
        /// </summary>
        public override FEMMesh FromGraph(GraphMesh.Graph graphToConvert)
        {
            if (graphToConvert == null) throw new ArgumentNullException(nameof(graphToConvert));
            if (graphToConvert.Vertices.Count < 3)
                throw new InvalidOperationException("Graph has too few vertices to build a FEM mesh.");

            // Create one FEMVertex per GraphFEMVertex
            var vtx = new List<FEMVertex>(graphToConvert.Vertices.Count);
            var idMap = new Dictionary<int, FEMVertex>(graphToConvert.Vertices.Count);

            int vid = 0;
            foreach (var gv in graphToConvert.Vertices)
            {
                var v = new FEMVertex
                {
                    GlobalId = vid++,
                    X = gv.X,
                    Y = gv.Y,
                    Potential = gv.Potential
                };
                vtx.Add(v);
                idMap[gv.GlobalId] = v;
            }

            // Build adjacency to detect triangles: a triangle is a triple (i,j,k) with all 3 edges present
            var adj = new Dictionary<int, HashSet<int>>();
            var wmap = new Dictionary<(int, int), double>(); // edge weight lookup (min,max) -> w

            foreach (var e in graphToConvert.Edges)
            {
                int gi = e.Vertices[0].GlobalId;
                int gj = e.Vertices[1].GlobalId;
                int i = idMap[gi].GlobalId;
                int j = idMap[gj].GlobalId;
                if (i == j) continue;

                if (!adj.TryGetValue(i, out var si)) adj[i] = si = new HashSet<int>();
                if (!adj.TryGetValue(j, out var sj)) adj[j] = sj = new HashSet<int>();
                si.Add(j); sj.Add(i);

                var key = (Math.Min(i, j), Math.Max(i, j));
                wmap[key] = e.Weight;
            }

            var elems = new List<FEMElement>();
            int eid = 0;

            // Find cliques of size 3
            for (int i = 0; i < vtx.Count; i++)
            {
                if (!adj.TryGetValue(i, out var Ni)) continue;
                foreach (var j in Ni.Where(j => j > i))
                {
                    if (!adj.TryGetValue(j, out var Nj)) continue;
                    // common neighbors k > j
                    foreach (var k in Ni.Intersect(Nj).Where(k => k > j))
                    {
                        var a = vtx[i];
                        var b = vtx[j];
                        var c = vtx[k];

                        // Skip degenerate/collinear triples
                        double area2 = Math.Abs((b.X - a.X) * (c.Y - a.Y) - (b.Y - a.Y) * (c.X - a.X));
                        if (area2 <= 1e-14) continue;

                        var el = new FEMElement(eid++, a, b, c);

                        // Set element conductivity as average of the 3 edge weights of the triangle
                        double w1 = wmap.TryGetValue((Math.Min(i, j), Math.Max(i, j)), out var w_ij) ? w_ij : 1.0;
                        double w2 = wmap.TryGetValue((Math.Min(j, k), Math.Max(j, k)), out var w_jk) ? w_jk : 1.0;
                        double w3 = wmap.TryGetValue((Math.Min(k, i), Math.Max(k, i)), out var w_ki) ? w_ki : 1.0;
                        el.Conductivity = Math.Max((w1 + w2 + w3) / 3.0, 1e-6);

                        elems.Add(el);
                    }
                }
            }

            if (elems.Count == 0)
                throw new InvalidOperationException("No triangles could be formed from graph edges. Ensure the graph is planar and sufficiently connected.");

            var fem = new FEMMesh(vtx, elems);

            // Refresh distributions
            var cd = elems.ToDictionary(e => e.Id, e => e.Conductivity);
            fem.SetConductivityDistribution(new ConductivityDistribution(cd));

            var pd = vtx.ToDictionary(v => v.GlobalId, v => v.Potential);
            fem.SetPotentialDistribution(new PotentialDistribution(pd));

            return fem;
        }
    }
}
