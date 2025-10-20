using System;
using System.Collections.Generic;
using System.Linq;
using Utility.Classes.Factories;

namespace Utility.Classes.Discretizer.FiniteElementMesh
{
    public class FEMMesh : Discretization<FEMElement, FEMElectrode>
    {
        public List<FEMVertex> Vertices { get; set; } = [];
        private readonly List<Edge> _edges = [];
        private readonly List<Edge> _boundaryEdges = [];
        private Dictionary<int, FEMVertex> _vertexLookup = new();
        private List<FEMVertex> _orderedBoundaryVertices = [];
        private Dictionary<int, int> _boundaryOrderLookup = new();

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

            NormalizeCoordinates();
            Initialize();
        }

        public FEMMesh()
        {
            Initialize();
        }

        public List<FEMVertex> GetVertices() => Vertices;

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
                if (rangeX < 1e-12)
                {
                    vertex.X = midpoint;
                }
                else
                {
                    double normalizedX = (vertex.X - minX) / rangeX;
                    vertex.X = minValue + normalizedX * targetRange;
                }

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

        private void RebuildVertexLookup()
        {
            _vertexLookup = Vertices.ToDictionary(v => v.GlobalId);
        }

        private void BuildEdgeTopology()
        {
            _edges.Clear();
            _boundaryEdges.Clear();

            var edgeUsage = new Dictionary<(int a, int b), (Edge edge, int count)>();
            int edgeId = 0;

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

        private void BuildOrderedBoundaryVertices()
        {
            _orderedBoundaryVertices = [];
            _boundaryOrderLookup = new Dictionary<int, int>();

            var boundaryVerts = Vertices.Where(v => v.IsBoundary).ToList();
            if (boundaryVerts.Count == 0)
                return;

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
                double cx = boundaryVerts.Average(v => v.X);
                double cy = boundaryVerts.Average(v => v.Y);
                ordered = [.. boundaryVerts.OrderBy(v => Math.Atan2(v.Y - cy, v.X - cx))];
            }
            else
            {
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

                    if (current == start.GlobalId)
                        break;
                }

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

        protected override IEnumerable<int> StateKeys() => Vertices.Select(v => v.GlobalId);

        protected override void ApplyPotentialToState(int stateKey, double potential)
        {
            var v = Vertices.FirstOrDefault(x => x.GlobalId == stateKey)
                    ?? throw new InvalidOperationException($"No FEMVertex.GlobalId = {stateKey}.");
            v.Potential = potential;
        }

        protected override double ReadPotentialOf(FEMElectrode e)
        {
            if (!e.PointElectrode && e.FEMVertexIds != null && e.FEMVertexIds.Count > 0)
            {
                return e.FEMVertexIds
                        .Select(id => Vertices.FirstOrDefault(v => v.GlobalId == id)
                                      ?? throw new InvalidOperationException($"No FEMVertex.GlobalId = {id}."))
                        .Select(v => v.Potential)
                        .Average();
            }

            var vv = Vertices.FirstOrDefault(v => v.GlobalId == e.MeshId)
                     ?? throw new InvalidOperationException($"No FEMVertex.GlobalId = {e.MeshId} (FEMElectrode.MeshId).");
            return vv.Potential;
        }

        public void PlaceEquidistantElectrodes(int numElectrodes, double zContact, double lengthHint, int nodesPerElectrode = 1)
        {
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

            numElectrodes = Math.Clamp(numElectrodes, 1, boundary.Count);
            nodesPerElectrode = Math.Clamp(nodesPerElectrode, 1, Math.Max(1, boundary.Count / numElectrodes));

            var used = new bool[boundary.Count];
            var electrodes = new List<FEMElectrode>(numElectrodes);
            double step = boundary.Count / (double)numElectrodes;
            double pos = 0.0;

            for (int i = 0; i < numElectrodes; i++)
            {
                int startIndex = FindNextUnusedIndex(used, (int)Math.Round(pos) % boundary.Count);
                if (startIndex < 0)
                    break;

                var assigned = new List<int>(nodesPerElectrode);
                int current = startIndex;
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

                var electrode = new FEMElectrode(
                    id: i,
                    meshId: assigned[0],
                    current: 0.0,
                    zContact: zContact,
                    voltage: 0.0,
                    pointElectrode: assigned.Count == 1);
                electrode.PointElectrode = assigned.Count == 1;
                electrode.FEMVertexIds.AddRange(assigned);
                double arcLength = ComputeElectrodeLength(assigned);
                electrode.Length = arcLength > 0.0 ? arcLength : lengthHint;
                electrodes.Add(electrode);

                pos += step;
            }

            for (int idx = 0; idx < boundary.Count; idx++)
            {
                if (!used[idx])
                {
                    boundary[idx].IsElectrode = false;
                    boundary[idx].ElectrodeId = -1;
                }
            }

            SetElectrodes(electrodes);
        }

        public IReadOnlyList<FEMVertex> GetOrderedBoundaryVertices() => _orderedBoundaryVertices;

        public bool TryGetBoundaryIndex(int vertexId, out int index) => _boundaryOrderLookup.TryGetValue(vertexId, out index);

        public FEMVertex GetVertexById(int vertexId)
            => _vertexLookup.TryGetValue(vertexId, out var vertex)
                ? vertex
                : throw new InvalidOperationException($"No FEMVertex.GlobalId = {vertexId}.");

        public List<int> OrderVerticesAlongBoundary(IEnumerable<int> vertexIds)
        {
            if (vertexIds == null)
                return [];

            var ids = vertexIds.Where(id => _boundaryOrderLookup.ContainsKey(id)).Distinct().ToList();
            if (ids.Count <= 1)
                return ids;

            var ordered = new List<int>(ids.Count);
            var idSet = new HashSet<int>(ids);

            foreach (var vertex in _orderedBoundaryVertices)
            {
                if (idSet.Contains(vertex.GlobalId))
                    ordered.Add(vertex.GlobalId);
            }

            if (ordered.Count <= 1)
                return ordered;

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

        public double ComputeElectrodeLength(IList<int> orderedVertexIds)
        {
            if (orderedVertexIds == null || orderedVertexIds.Count == 0)
                return 0.0;

            if (orderedVertexIds.Count == 1)
            {
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

        private static double Distance(FEMVertex a, FEMVertex b)
        {
            double dx = a.X - b.X;
            double dy = a.Y - b.Y;
            return Math.Sqrt(dx * dx + dy * dy);
        }


        /// <summary>
        /// Creates a deep copy of this FEMMesh, including vertices, elements,
        /// electrode list, and distributions.
        /// </summary>
        public override Discretization DeepCopy()
        {
            var FEMVertexMap = new Dictionary<int, FEMVertex>(Vertices.Count);
            var newVertices = new List<FEMVertex>(Vertices.Count);

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
                    if (e.FEMVertexIds?.Count > 0)
                        e2.FEMVertexIds.AddRange(e.FEMVertexIds);

                    electrodeCopies.Add(e2);
                }
                copy.SetElectrodes(electrodeCopies);
            }
            else
            {
                copy.SetElectrodes(new List<FEMElectrode>());
            }

            copy.SetConductivityDistribution(new ConductivityDistribution(new Dictionary<int, double>(this.ConductivityDistribution.Conductivities)));
            copy.SetPotentialDistribution(new PotentialDistribution(new Dictionary<int, double>(this.PotentialDistribution.Potentials)));
            return copy;
        }

        public override void LogDiscretization()
        {
            Console.WriteLine($"FEM | V={Vertices.Count}, E={_elements.Count}, EL={_electrodes.Count}");
        }

        /// <summary>
        /// Split every triangle into four by inserting midpoints on each edge.
        /// Potentials at new midpoints are averaged from endpoints; conductivity is inherited from parent.
        /// </summary>
        public override FEMMesh RefineUniform(int levels = 1)
        {
            var current = (FEMMesh)this.DeepCopy();
            for (int L = 0; L < Math.Max(1, levels); L++)
                current = current.RefineOnce();
            return current;
        }

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
            }

            // Refresh distributions
            var cd = refined.ElementsTyped.Cast<FEMElement>().ToDictionary(e => e.Id, e => e.Conductivity);
            refined.SetConductivityDistribution(new ConductivityDistribution(cd));

            var pd = refined.Vertices.ToDictionary(v => v.GlobalId, v => v.Potential);
            refined.SetPotentialDistribution(new PotentialDistribution(pd));

            return refined;
        }


        /// <summary>
        /// Iterates through all elements and creates a graph based on if two elements share a face
        /// then the two graph nodes will be connected with a weigth of the two elements harmonic mean conductance.
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
