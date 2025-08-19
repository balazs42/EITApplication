using Utility.Classes.Factories;
using Utility.Classes.Meshing.LatticeBoltzmannMesh;

namespace Utility.Classes.Meshing.FiniteElementMesh
{
    public class FEMMesh : Mesh<FEMElement, FEMElectrode>
    {
        public List<FEMVertex> Vertices { get; set; } = [];
        private List<Edge> _edges { get; set; } = [];

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

            Initialize();
        }

        public FEMMesh()
        {
            Initialize();
        }

        public List<FEMVertex> GetVertice() => Vertices;

        public void Initialize()
        {
            // Initialize with a homogeneous conductivity distribution
            ConductivityDistribution = ConductivityDistributionFactory.FromFEMMesh(this);

            Dictionary<int, double> potentialDistribution = new Dictionary<int, double>();

            foreach (var FEMVertex in Vertices)
                potentialDistribution.Add(FEMVertex.GlobalId, FEMVertex.Potential);

            PotentialDistribution = new PotentialDistribution(potentialDistribution);

            int edgeId = 0;

            // Add edges
            foreach(var element in _elements)
            {
                _edges.Add(new Edge(element.Vertices[0], element.Vertices[1], edgeId++));
                _edges.Add(new Edge(element.Vertices[0], element.Vertices[2], edgeId++));
                _edges.Add(new Edge(element.Vertices[1], element.Vertices[2], edgeId++));
            }

            // Remove duplicates
            foreach(var edge in _edges.ToList())
                _edges.RemoveAll(x => x.Start.X == edge.Start.X && x.Start.Y == edge.Start.Y && x.Id != edgeId ||
                                     x.Start.Y == edge.Start.X && x.Start.X == edge.Start.Y && x.Id != edgeId);
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


        /// <summary>
        /// Creates a deep copy of this FEMMesh, including vertices, elements,
        /// electrode list, and distributions.
        /// </summary>
        public override Mesh DeepCopy()
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

            var copy = new FEMMesh(newVertices, newElements);

            if (_electrodes.Count > 0)
            {
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

                    copy.ElectrodesTyped.ToList().Add(e2); 
                }
            }

            copy.SetElectrodes(_electrodes);
            copy.SetConductivityDistribution(new ConductivityDistribution(this.ConductivityDistribution.Conductivities));
            copy.SetPotentialDistribution(new PotentialDistribution(this.PotentialDistribution.Potentials));

            return copy;
        }

        public override void LogMesh()
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
        public override Utility.Classes.Meshing.GraphMesh.Graph ToGraph()
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
            var gVertices = new List<Utility.Classes.Meshing.Graph.Graph.GraphFEMVertex>(ne);
            for (int ei = 0; ei < ne; ei++)
            {
                var el = elements[ei];
                var (cx, cy) = Centroid(el);
                int domainId = 0;               
                int boundaryId = isBoundaryElement[ei] ? 1 : 0;
                // Use element Id for GlobalId so it’s easy to map back
                gVertices.Add(new Utility.Classes.Meshing.Graph.Graph.GraphFEMVertex(cx, cy, el.Id, domainId, boundaryId));
            }

            // 2) Build edges between neighboring elements that share a mesh edge,
            //    weight = |Γ| / (d_i/σ_i + d_j/σ_j) (two-point flux)
            var idToIndex = elements.ToDictionary(e => e.Id, e => elements.IndexOf(e));
            var gEdges = new List<Utility.Classes.Meshing.Graph.Graph.GraphEdge>();

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
                gEdges.Add(new Utility.Classes.Meshing.Graph.Graph.GraphEdge(Gi, Gj, tau));
            }

            return new Utility.Classes.Meshing.GraphMesh.Graph(gVertices, gEdges);
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
