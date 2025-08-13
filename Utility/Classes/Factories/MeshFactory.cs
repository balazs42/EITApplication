using Utility.Classes.Meshing;
using MIConvexHull;
using Utility.Classes.Meshing.FiniteElementMesh;
using Utility.Classes.Meshing.LatticeBoltzmannMesh;

namespace Utility.Classes.Factories
{
    /// <summary>
    /// General mesh descriptor that can be used in both cases of the meshes
    /// </summary>
    public struct MeshParameters
    {
        public MeshType MeshType { get; set; }
        public int Layers { get; set; }
        public int BoundaryVertexCount { get; set; }
        public int ElectrodeCount { get; set; }
        public int Nx { get; set; }
        public int Ny { get; set; }
        public List<Dictionary<int, double>> Inhomogenities { get; set; }
        public int Radius { get; set; }
    }

    /// <summary>
    /// The mesh factory can be used to genreate Finite Element meshes and Lattice Boltzmann meshes
    /// TODO: add generic perimeter description based mesh generation
    /// </summary>
    public static class MeshFactory
    {
        public static IMesh Create(MeshParameters parameters, double inhomogenityValue = 1.0) => parameters.MeshType switch
        {
            MeshType.FEM => CreateCircularFEMMesh(layers: parameters.Layers,
                                                  boundaryVertexCount: parameters.BoundaryVertexCount,
                                                  electrodeCount: parameters.ElectrodeCount,
                                                  inhomogeneityValue: inhomogenityValue),
            MeshType.LBM => LBMCreateCircular(parameters.Nx, parameters.Ny, parameters.Radius, parameters.ElectrodeCount),
            _ => throw new NotSupportedException()
        };

        #region Finite Element Mesh Generation

        /// <summary>
        /// Builds a circular FEM mesh with given concentric layers and boundary vertices,
        /// then distributes `electrodeCount` electrodes evenly around the outer boundary.
        /// </summary>
        public static FEMMesh CreateCircularFEMMesh(int layers, int boundaryVertexCount, int electrodeCount = 16)
        {
            return CreateCircularFEMMeshInternal(layers, boundaryVertexCount, electrodeCount, inhomogeneityValue: 3.0);
        }

        /// <summary>
        /// Builds an inhomogeneous circular FEM mesh where elements in the inner rings
        /// have conductivity scaled by inhomogeneityValue (default 3.0).
        /// </summary>
        public static FEMMesh CreateCircularFEMMesh(int layers, int boundaryVertexCount, int electrodeCount = 16, double inhomogeneityValue = 3.0)
        {
            return CreateCircularFEMMeshInternal(layers, boundaryVertexCount, electrodeCount, inhomogeneityValue);
        }

        // common implementation with inhomogeneity scaling
        private static FEMMesh CreateCircularFEMMeshInternal(int layers, int boundaryVertexCount, int electrodeCount, double inhomogeneityValue)
        {
            if (electrodeCount > boundaryVertexCount)
                electrodeCount = boundaryVertexCount;

            // 1) build vertices (center + rings)
            var vertices = new List<Vertex>();
            int vid = 0;

            // center
            vertices.Add(new Vertex(vid++, 0, 0) { IsBoundary = (layers == 0) });

            // concentric rings
            for (int layer = 1; layer <= layers; layer++)
            {
                double rnorm = (double)layer / layers;
                for (int i = 0; i < boundaryVertexCount; i++)
                {
                    double theta = 2 * Math.PI * i / boundaryVertexCount;
                    vertices.Add(new Vertex(globalId: vid++,
                                            x: rnorm * Math.Cos(theta),
                                            y: rnorm * Math.Sin(theta))
                    {
                        IsBoundary = (layer == layers),
                        BoundaryId = (layer == layers ? i : -1)
                    });
                }
            }

            // 2) triangulate
            var triVerts = vertices.Select(v => new TriVertex(v)).ToArray();
            var delaunay = DelaunayTriangulation<TriVertex, DefaultTriangulationCell<TriVertex>>.Create(triVerts, 1e-3);

            // 3) create elements with inhomogeneous conductivity
            var elements = new List<FEMElement>();
            int eid = 0;
            double innerRadius = 1.0 / (layers + 1e-4); // threshold

            foreach (var cell in delaunay.Cells)
            {
                var a = cell.Vertices[0].Original;
                var b = cell.Vertices[1].Original;
                var c = cell.Vertices[2].Original;

                var elem = new FEMElement(eid++, a, b, c);

                // compute average radial distance of element vertices
                double rA = Math.Sqrt(a.X * a.X + a.Y * a.Y);
                double rB = Math.Sqrt(b.X * b.X + b.Y * b.Y);
                double rC = Math.Sqrt(c.X * c.X + c.Y * c.Y);

                double avgRadius = (rA + rB + rC) / 3.0;

                // if inside inner circle, scale conductivity
                if (avgRadius < innerRadius)
                    elem.Conductivity = inhomogeneityValue;
                else
                    elem.Conductivity = 1.0;

                elements.Add(elem);
            }

            // 4) assemble mesh
            var mesh = new FEMMesh(vertices, elements);

            // 5) distribute electrodes

            // clear any leftover flags
            foreach (var v in vertices)
            {
                v.IsElectrode = false;
                v.ElectrodeId = -1;
            }

            // gather boundary vertices sorted by their BoundaryId
            var boundaryVerts = vertices
                .Where(v => v.IsBoundary)
                .OrderBy(v => v.BoundaryId)
                .ToList();
            int boundaryCount = boundaryVerts.Count;
            int increment = Math.Max(boundaryCount / electrodeCount, 1);

            var electrodes = new List<FEMElectrode>(electrodeCount);
            for (int elId = 0; elId < electrodeCount; elId++)
            {
                // pick every 'increment' boundary vertex
                int idx = elId * increment;
                if (idx >= boundaryCount) idx = boundaryCount - 1;

                Vertex v = boundaryVerts[idx];
                v.IsElectrode = true;
                v.ElectrodeId = elId;

                var el = new FEMElectrode(
                    id: elId,
                    meshId: v.GlobalId,
                    current: 0.0,
                    zContact: 0.1,
                    voltage: 1.0);

                el.VertexIds.Add(v.GlobalId);
                electrodes.Add(el);
            }

            mesh.SetElectrodes(electrodes);

            // Assing vertex neighbors
            foreach (var element in elements)
            {
                Vertex V1 = element.Vertices[0];
                Vertex V2 = element.Vertices[1];
                Vertex V3 = element.Vertices[2];

                //  ---------------
                //  |             |
                // v1 --- v2 --- v3
                V1.Neighbors.Add(V2); V1.Neighbors.Add(V3);
                V2.Neighbors.Add(V1); V2.Neighbors.Add(V3);
                V3.Neighbors.Add(V1); V3.Neighbors.Add(V2);
            }

            // Remove duplicates
            foreach (var element in elements)
            {
                for (int i = 0; i < 3; i++)
                {
                    // Find any duplicates
                    Vertex v = element.Vertices[i];

                    for (int j = 0; j < v.Neighbors.Count; j++)
                    {
                        List<Vertex> neighbors = v.Neighbors;
                        for (int k = 0; k < v.Neighbors.Count; k++)
                        {
                            if ((neighbors[k].GlobalId == neighbors[j].GlobalId) && k != j)
                            {
                                neighbors.RemoveAt(k);
                                j = 0;
                                k = 0;
                                break;
                            }
                        }
                    }
                }
            }

            // Initialize should be called, because that is what updates the mesh conductivity distribution and potential distribution
            mesh.Initialize();
            return mesh;
        }

        /// <summary>
        /// Apply multiple inhomogeneity dictionaries to a FEM mesh.
        /// Later overrides have precedence.
        /// </summary>
        public static void ApplyFEMInhomogenities(FEMMesh mesh, List<Dictionary<int, double>> inhomogenities)
        {
            var baseDist = mesh.GetConductivityDistribution();
            var dict = new Dictionary<int, double>(baseDist.Conductivities);
            foreach (var d in inhomogenities)
                foreach (var kv in d)
                    if (dict.ContainsKey(kv.Key))
                        dict[kv.Key] = kv.Value;
            mesh.SetConductivityDistribution(new ConductivityDistribution(dict));
        }

        private class TriVertex : IVertex
        {
            public double[] Position { get; }
            public Vertex Original { get; }

            public TriVertex(Vertex v)
            {
                Original = v;
                Position = new[] { v.X, v.Y };
            }
        }

        #endregion

        #region Lattice Boltzmann Mesh Generation

        private static LBMMesh LBMCreateRectangularWithBorder(int nx = 15, int ny = 15, int electrodeCount = 16)
        {
            return new LBMMesh(nx, ny);
        }


        private static LBMMesh LBMCreateRectangularWithInhomogenity(int nx = 15, int ny = 15, int electrodeCount = 16, double inhomogenityValue = 1.0, int inhomogenitySize = 4)
        {
            if (inhomogenitySize > nx || inhomogenitySize > ny)
                throw new ArgumentOutOfRangeException("Cannot create LBM mesh with inhomogenity, size too big!");

            LBMMesh mesh = new LBMMesh(nx, ny, electrodeCount);

            // 2) overlay a centered square of altered conductivity
            int cx = nx / 2, cy = ny / 2;
            int half = inhomogenitySize / 2;
            for (int y = cy - half; y <= cy + half; y++)
            {
                for (int x = cx - half; x <= cx + half; x++)
                {
                    // bounds‐check
                    if (x < 0 || x >= nx || y < 0 || y >= ny) 
                        continue;

                    var el = mesh.GetElementAt(x, y);
                    el.Conductivity = inhomogenityValue;
                }
            }

            // 3) rebuild distribution so downstream code sees it
            var cd = mesh.GetElements().ToDictionary(e => e.Id, e => e.Conductivity);
            mesh.SetConductivityDistribution(new ConductivityDistribution(cd));

            return mesh;
        }

        /// <summary>
        /// Creates a default rectangular mesh, then sets the walls, such that the domain is circular, and 
        /// re initializes the electrode locations.
        /// </summary>
        /// <param name="nx">Number of x direction pixels/cells.</param>
        /// <param name="ny">Number of y direction pixels/cells.</param>
        /// <param name="radius">Radius of the inner circle.</param>
        /// <param name="electrodeCount">Number of electrodes to distribute.</param>
        /// <returns></returns>
        private static LBMMesh LBMCreateCircular(int nx = 15, int ny = 15, int radius = 10, int electrodeCount = 16)
        {
            if (radius > nx / 2 || radius > ny / 2)
                throw new ArgumentOutOfRangeException(nameof(radius),
                    "Cannot create circular LBM mesh: radius too big.");

            // 1) build rectangular then carve circle
            var mesh = new LBMMesh(nx, ny, electrodeCount);

            double cx = (nx - 1) / 2.0;
            double cy = (ny - 1) / 2.0;
            double r2 = radius * radius;

            var elements = mesh.GetElements().Cast<LBMElement>();

            // mark anything outside circle as wall
            foreach (var el in elements)
            {
                var (x, y) = mesh.ToLattice(el.Id);
                double dx = x - cx;
                double dy = y - cy;
                if (dx * dx + dy * dy > r2)
                {
                    el.IsWall = true;
                    el.IsElectrode = false;
                }
                else
                {
                    el.IsWall = false;
                }
            }

            // 2) rebuild electrode list along the new inner perimeter            
            mesh.PlaceEquidistantElectrodes(electrodeCount);

            // 3) refresh conductivity distribution
            var cd = mesh.GetElements().ToDictionary(e => e.Id, e => e.Conductivity);
            mesh.SetConductivityDistribution(new ConductivityDistribution(cd));

            return mesh;
        }


        #endregion
    }
}
