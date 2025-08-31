using Utility.Classes.Meshing;
using MIConvexHull;
using Utility.Classes.Meshing.FiniteElementMesh;
using Utility.Classes.Meshing.LatticeBoltzmannMesh;
using Utility.Classes.Application;
using System.Collections.Generic;

namespace Utility.Classes.Factories
{
    /// <summary>
    /// The mesh factory can be used to genreate Finite Element meshes and Lattice Boltzmann meshes
    /// TODO: add generic perimeter description based mesh generation
    /// </summary>
    public static class MeshFactory
    {
        public static IMesh Create(MeshParameters parameters, double inhomogenityValue = 1.0) => parameters.MeshType switch
        {
            MeshType.FEM => CreateCircularFEMMesh(layers: parameters.Layers,
                                                  boundaryFEMVertexCount: parameters.BoundaryFEMVertexCount,
                                                  electrodeCount: parameters.ElectrodeCount,
                                                  inhomogeneityValue: inhomogenityValue),
            MeshType.LBM => LBMCreateCircular(parameters.Nx, parameters.Ny, parameters.Radius, parameters.ElectrodeCount),
            _ => throw new NotSupportedException()
        };

        public static IMesh CreateDefault(MeshParameters parameters) => Create(parameters, 1.0);
        

        #region Finite Element Mesh Generation

        /// <summary>
        /// Builds a circular FEM mesh with given concentric layers and boundary vertices,
        /// then distributes `electrodeCount` electrodes evenly around the outer boundary.
        /// </summary>
        public static FEMMesh CreateCircularFEMMesh(int layers, int boundaryFEMVertexCount, int electrodeCount = 16)
        {
            var mesh = CreateCircularFEMMeshInternal(layers, boundaryFEMVertexCount, electrodeCount, inhomogeneityValue: 3.0);

            mesh.Metadata.Generator = nameof(CreateCircularFEMMesh);
            mesh.Metadata.Parameters["layers"] = layers.ToString();
            mesh.Metadata.Parameters["boundaryFEMVertexCount"] = boundaryFEMVertexCount.ToString();
            mesh.Metadata.Parameters["electrodeCount"] = electrodeCount.ToString();

            return mesh;
        }

        /// <summary>
        /// Builds an inhomogeneous circular FEM mesh where elements in the inner rings
        /// have conductivity scaled by inhomogeneityValue (default 3.0).
        /// </summary>
        public static FEMMesh CreateCircularFEMMesh(int layers, int boundaryFEMVertexCount, int electrodeCount = 16, double inhomogeneityValue = 3.0)
        {
            var mesh = CreateCircularFEMMeshInternal(layers, boundaryFEMVertexCount, electrodeCount, inhomogeneityValue);

            mesh.Metadata.Generator = nameof(CreateCircularFEMMesh);
            mesh.Metadata.Parameters["layers"] = layers.ToString();
            mesh.Metadata.Parameters["boundaryFEMVertexCount"] = boundaryFEMVertexCount.ToString();
            mesh.Metadata.Parameters["electrodeCount"] = electrodeCount.ToString();
            mesh.Metadata.Parameters["inhomogeneityValue"] = inhomogeneityValue.ToString();

            Workspace.AddLogMessage("MeshFactory", "Created Ciruclar FEMMesh object");

            return mesh;
        }

        // common implementation with inhomogeneity scaling
        private static FEMMesh CreateCircularFEMMeshInternal(int layers, int boundaryFEMVertexCount, int electrodeCount, double inhomogeneityValue)
        {
            if (electrodeCount > boundaryFEMVertexCount)
                electrodeCount = boundaryFEMVertexCount;

            // 1) build vertices (center + rings)
            var vertices = new List<FEMVertex>();
            int vid = 0;

            // center
            vertices.Add(new FEMVertex(vid++, 0, 0) { IsBoundary = (layers == 0) });

            // concentric rings
            for (int layer = 1; layer <= layers; layer++)
            {
                double rnorm = (double)layer / layers;
                for (int i = 0; i < boundaryFEMVertexCount; i++)
                {
                    double theta = 2 * Math.PI * i / boundaryFEMVertexCount;
                    vertices.Add(new FEMVertex(globalId: vid++,
                                               x: rnorm * Math.Cos(theta),
                                               y: rnorm * Math.Sin(theta))
                    {
                        IsBoundary = (layer == layers),
                        BoundaryId = (layer == layers ? i : -1)
                    });
                }
            }

            // 2) triangulate
            var triVerts = vertices.Select(v => new TriFEMVertex(v)).ToArray();
            var delaunay = DelaunayTriangulation<TriFEMVertex, DefaultTriangulationCell<TriFEMVertex>>.Create(triVerts, 1e-3);

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

            // use fractional steps so electrodes remain evenly spaced even
            // when boundaryCount isn't divisible by electrodeCount
            double step = boundaryCount / (double)electrodeCount;
            double pos = 0.0;

            var electrodes = new List<FEMElectrode>(electrodeCount);
            for (int elId = 0; elId < electrodeCount; elId++)
            {
                int idx = (int)Math.Round(pos, MidpointRounding.AwayFromZero);
                if (idx >= boundaryCount)
                    idx = boundaryCount - 1;

                FEMVertex v = boundaryVerts[idx];
                v.IsElectrode = true;
                v.ElectrodeId = elId;

                var el = new FEMElectrode(
                    id: elId,
                    meshId: v.GlobalId,
                    current: 0.0,
                    zContact: 0.1,
                    voltage: 1.0);

                el.FEMVertexIds.Add(v.GlobalId);
                electrodes.Add(el);

                pos += step;
            }

            mesh.SetElectrodes(electrodes);

            // Assing FEMVertex neighbors
            foreach (var element in elements)
            {
                FEMVertex V1 = element.Vertices[0];
                FEMVertex V2 = element.Vertices[1];
                FEMVertex V3 = element.Vertices[2];

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
                    FEMVertex v = element.Vertices[i];

                    for (int j = 0; j < v.Neighbors.Count; j++)
                    {
                        List<FEMVertex> neighbors = v.Neighbors;
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

            Workspace.AddLogMessage("MeshFactory", "Added inhomogenities to FEMMesh object's ConductivityDistribution.");
        }

        /// <summary>
        /// Create a FEM mesh based on an ordered list of perimeter points forming a single loop.
        /// The algorithm connects every consecutive boundary point with the centroid, producing
        /// a fan triangulation.  Electrodes are placed on equally spaced boundary vertices.
        /// </summary>
        public static FEMMesh CreatePolygonFEMMesh(IList<(double x, double y)> perimeter, int electrodeCount = 16)
        {
            ValidatePerimeter(perimeter);

            var vertices = new List<FEMVertex>();
            int vid = 0;

            double cx = perimeter.Average(p => p.x);
            double cy = perimeter.Average(p => p.y);
            var center = new FEMVertex(vid++, cx, cy);
            vertices.Add(center);

            for (int i = 0; i < perimeter.Count; i++)
            {
                var p = perimeter[i];
                vertices.Add(new FEMVertex(vid++, p.x, p.y)
                {
                    IsBoundary = true,
                    BoundaryId = i
                });
            }

            var elements = new List<FEMElement>();
            int eid = 0;
            for (int i = 0; i < perimeter.Count; i++)
            {
                var v2 = vertices[i + 1];
                var v3 = vertices[i + 2 > perimeter.Count ? 1 : i + 2];
                elements.Add(new FEMElement(eid++, center, v2, v3));
            }

            var mesh = new FEMMesh(vertices, elements);

            var boundaryVerts = vertices.Skip(1).ToList();
            electrodeCount = Math.Min(electrodeCount, boundaryVerts.Count);
            int step = Math.Max(boundaryVerts.Count / electrodeCount, 1);

            var electrodes = new List<FEMElectrode>();
            for (int e = 0; e < electrodeCount; e++)
            {
                var v = boundaryVerts[e * step];
                v.IsElectrode = true;
                v.ElectrodeId = e;

                var el = new FEMElectrode(
                    id: e,
                    meshId: v.GlobalId,
                    current: 0.0,
                    zContact: 0.1,
                    voltage: 1.0);
                el.FEMVertexIds.Add(v.GlobalId);
                electrodes.Add(el);
            }

            mesh.SetElectrodes(electrodes);

            foreach (var element in elements)
            {
                var V1 = element.Vertices[0];
                var V2 = element.Vertices[1];
                var V3 = element.Vertices[2];
                V1.Neighbors.Add(V2); V1.Neighbors.Add(V3);
                V2.Neighbors.Add(V1); V2.Neighbors.Add(V3);
                V3.Neighbors.Add(V1); V3.Neighbors.Add(V2);
            }

            foreach (var element in elements)
            {
                for (int i = 0; i < 3; i++)
                {
                    var v = element.Vertices[i];
                    for (int j = 0; j < v.Neighbors.Count; j++)
                    {
                        for (int k = j + 1; k < v.Neighbors.Count; k++)
                        {
                            if (v.Neighbors[j].GlobalId == v.Neighbors[k].GlobalId)
                            {
                                v.Neighbors.RemoveAt(k);
                                k--;
                            }
                        }
                    }
                }
            }

            mesh.Initialize();

            mesh.Metadata.Generator = nameof(CreatePolygonFEMMesh);
            mesh.Metadata.Parameters["electrodeCount"] = electrodeCount.ToString();
            mesh.Metadata.Parameters["perimeter"] = string.Join(";", perimeter.Select(p => $"{p.x},{p.y}"));

            return mesh;
        }

        /// <summary>
        /// Convenience wrapper that builds a rectangular FEM mesh from corner points.
        /// </summary>
        public static FEMMesh CreateRectangularFEMMesh(double width, double height, int electrodeCount = 16)
        {
            var hw = width / 2.0;
            var hh = height / 2.0;
            var pts = new List<(double x, double y)>
            {
                (-hw, -hh),
                ( hw, -hh),
                ( hw,  hh),
                (-hw,  hh)
            };
            var mesh = CreatePolygonFEMMesh(pts, electrodeCount);
            mesh.Metadata.Generator = nameof(CreateRectangularFEMMesh);
            mesh.Metadata.Parameters["width"] = width.ToString();
            mesh.Metadata.Parameters["height"] = height.ToString();
            mesh.Metadata.Parameters["electrodeCount"] = electrodeCount.ToString();
            return mesh;
        }

        /// <summary>
        /// Create a FEM mesh from an arbitrary thorax-shaped perimeter.
        /// </summary>
        public static FEMMesh CreateThoraxFEMMesh(IList<(double x, double y)> perimeter, int electrodeCount = 16)
        {
            var mesh = CreatePolygonFEMMesh(perimeter, electrodeCount);
            mesh.Metadata.Generator = nameof(CreateThoraxFEMMesh);
            mesh.Metadata.Parameters["electrodeCount"] = electrodeCount.ToString();
            mesh.Metadata.Parameters["perimeter"] = string.Join(";", perimeter.Select(p => $"{p.x},{p.y}"));
            return mesh;
        }

        private class TriFEMVertex : IVertex
        {
            public double[] Position { get; }
            public FEMVertex Original { get; }

            public TriFEMVertex(FEMVertex v)
            {
                Original = v;
                Position = new[] { v.X, v.Y };
            }
        }

        #endregion

        #region Lattice Boltzmann Mesh Generation
        public static LBMMesh CreateLBMMeshFromPerimeter(int nx, int ny, IList<(double x, double y)> perimeter, int electrodeCount = 16)
        {
            ValidatePerimeter(perimeter);
            var mesh = new LBMMesh(nx, ny);

            var inside = new bool[nx, ny];
            for (int y = 0; y < ny; y++)
                for (int x = 0; x < nx; x++)
                    inside[x, y] = IsPointInPolygon(x + 0.5, y + 0.5, perimeter);

            for (int y = 0; y < ny; y++)
            {
                for (int x = 0; x < nx; x++)
                {
                    var el = mesh.GetElementAt(x, y);
                    bool cellInside = inside[x, y];
                    bool boundary = false;
                    if (cellInside)
                    {
                        boundary = x == 0 || x == nx - 1 || y == 0 || y == ny - 1 ||
                                   !inside[Math.Max(x - 1, 0), y] ||
                                   !inside[Math.Min(x + 1, nx - 1), y] ||
                                   !inside[x, Math.Max(y - 1, 0)] ||
                                   !inside[x, Math.Min(y + 1, ny - 1)];
                    }
                    el.IsWall = !cellInside || boundary;
                    el.IsElectrode = false;
                }
            }

            // place electrodes evenly around the domain boundary
            mesh.PlaceEquidistantElectrodes(electrodeCount);

            var cd = mesh.GetElements().ToDictionary(e => e.Id, e => e.Conductivity);
            mesh.SetConductivityDistribution(new ConductivityDistribution(cd));

            Workspace.AddLogMessage("MeshFactory", "Created LBMMesh from Perimeter definition.");

            mesh.Metadata.Generator = nameof(CreateLBMMeshFromPerimeter);
            mesh.Metadata.Parameters["nx"] = nx.ToString();
            mesh.Metadata.Parameters["ny"] = ny.ToString();
            mesh.Metadata.Parameters["electrodeCount"] = electrodeCount.ToString();
            mesh.Metadata.Parameters["perimeter"] = string.Join(";", perimeter.Select(p => $"{p.x},{p.y}"));

            return mesh;
        }

        public static LBMMesh CreateRectangularLBMMesh(int nx, int ny, int electrodeCount = 16)
        {
            var mesh = new LBMMesh(nx, ny);

            // collect inner boundary cells (adjacent to outer walls)
            var boundary = new List<LBMElement>();
            for (int x = 1; x < nx - 1; x++)
                boundary.Add(mesh.GetElementAt(x, 1));
            for (int y = 2; y < ny - 1; y++)
                boundary.Add(mesh.GetElementAt(nx - 2, y));
            for (int x = nx - 3; x >= 1; x--)
                boundary.Add(mesh.GetElementAt(x, ny - 2));
            for (int y = ny - 3; y >= 2; y--)
                boundary.Add(mesh.GetElementAt(1, y));

            var electrodes = new List<LBMElectrode>();
            int count = Math.Min(electrodeCount, boundary.Count);
            if (count > 0)
            {
                int step = Math.Max(boundary.Count / count, 1);
                for (int i = 0; i < count; i++)
                {
                    var cell = boundary[i * step];
                    cell.IsElectrode = true;
                    electrodes.Add(new LBMElectrode(i, cell.Id, 0.0, 0.0, 0.0));
                }
            }

            mesh.SetElectrodes(electrodes);

            var cd = mesh.GetElements().ToDictionary(e => e.Id, e => e.Conductivity);
            mesh.SetConductivityDistribution(new ConductivityDistribution(cd));

            mesh.Metadata.Generator = nameof(CreateRectangularLBMMesh);
            mesh.Metadata.Parameters["nx"] = nx.ToString();
            mesh.Metadata.Parameters["ny"] = ny.ToString();
            mesh.Metadata.Parameters["electrodeCount"] = electrodeCount.ToString();
            return mesh;
        }

        public static LBMMesh CreateThoraxLBMMesh(int nx, int ny, IList<(double x, double y)> perimeter, int electrodeCount = 16)
        {
            var mesh = CreateLBMMeshFromPerimeter(nx, ny, perimeter, electrodeCount);
            mesh.Metadata.Generator = nameof(CreateThoraxLBMMesh);
            mesh.Metadata.Parameters["nx"] = nx.ToString();
            mesh.Metadata.Parameters["ny"] = ny.ToString();
            mesh.Metadata.Parameters["electrodeCount"] = electrodeCount.ToString();
            mesh.Metadata.Parameters["perimeter"] = string.Join(";", perimeter.Select(p => $"{p.x},{p.y}"));
            return mesh;
        }

        private static LBMMesh LBMCreateRectangularWithBorder(int nx = 15, int ny = 15, int electrodeCount = 16)
        {
            var mesh = new LBMMesh(nx, ny);
            mesh.Metadata.Generator = nameof(LBMCreateRectangularWithBorder);
            mesh.Metadata.Parameters["nx"] = nx.ToString();
            mesh.Metadata.Parameters["ny"] = ny.ToString();
            mesh.Metadata.Parameters["electrodeCount"] = electrodeCount.ToString();
            return mesh;
        }


        private static LBMMesh LBMCreateRectangularWithInhomogenity(int nx = 15, int ny = 15, int electrodeCount = 16, double inhomogenityValue = 1.0, int inhomogenitySize = 4)
        {
            if (inhomogenitySize > nx || inhomogenitySize > ny)
                throw new ArgumentOutOfRangeException("Cannot create LBM mesh with inhomogenity, size too big!");

            LBMMesh mesh = new LBMMesh(nx, ny);

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

            mesh.Metadata.Generator = nameof(LBMCreateRectangularWithInhomogenity);
            mesh.Metadata.Parameters["nx"] = nx.ToString();
            mesh.Metadata.Parameters["ny"] = ny.ToString();
            mesh.Metadata.Parameters["electrodeCount"] = electrodeCount.ToString();
            mesh.Metadata.Parameters["inhomogenityValue"] = inhomogenityValue.ToString();
            mesh.Metadata.Parameters["inhomogenitySize"] = inhomogenitySize.ToString();
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

            var mesh = new LBMMesh(nx, ny);

            double cx = (nx - 1) / 2.0;
            double cy = (ny - 1) / 2.0;
            double r2 = radius * radius;

            // Generate circle perimeter using midpoint (Minecraft) algorithm
            var circle = new HashSet<(int x, int y)>();
            void PlotCirclePoints(int px, int py)
            {
                if (px >= 0 && px < nx && py >= 0 && py < ny)
                    circle.Add((px, py));
            }

            int x = 0;
            int y = radius;
            int d = 3 - 2 * radius;
            while (y >= x)
            {
                PlotCirclePoints((int)(cx + x), (int)(cy + y));
                PlotCirclePoints((int)(cx - x), (int)(cy + y));
                PlotCirclePoints((int)(cx + x), (int)(cy - y));
                PlotCirclePoints((int)(cx - x), (int)(cy - y));
                PlotCirclePoints((int)(cx + y), (int)(cy + x));
                PlotCirclePoints((int)(cx - y), (int)(cy + x));
                PlotCirclePoints((int)(cx + y), (int)(cy - x));
                PlotCirclePoints((int)(cx - y), (int)(cy - x));

                if (d < 0)
                    d += 4 * x + 6;
                else
                {
                    d += 4 * (x - y) + 10;
                    y--;
                }
                x++;
            }

            var elements = mesh.GetElements().Cast<LBMElement>();

            foreach (var el in elements)
            {
                el.IsElectrode = false;
                var (ex, ey) = mesh.ToLattice(el.Id);
                double dx = ex - cx;
                double dy = ey - cy;
                double distSq = dx * dx + dy * dy;
                bool outside = distSq >= r2;
                bool onBoundary = circle.Contains((ex, ey));

                el.IsWall = outside || onBoundary || ex == 0 || ey == 0 || ex == nx - 1 || ey == ny - 1;
            }

            // Place electrodes on outermost non-wall layer
            mesh.PlaceEquidistantElectrodes(electrodeCount);

            // Refresh conductivity distribution
            var cd = mesh.GetElements().ToDictionary(e => e.Id, e => e.Conductivity);
            mesh.SetConductivityDistribution(new ConductivityDistribution(cd));

            Workspace.AddLogMessage("MeshFactory", "Created ciruclar LBMMesh object.");

            mesh.Metadata.Generator = nameof(LBMCreateCircular);
            mesh.Metadata.Parameters["nx"] = nx.ToString();
            mesh.Metadata.Parameters["ny"] = ny.ToString();
            mesh.Metadata.Parameters["radius"] = radius.ToString();
            mesh.Metadata.Parameters["electrodeCount"] = electrodeCount.ToString();

            return mesh;
        }


        #endregion

        public static void AddGaussianNoise(IMesh mesh)
        {
            if (mesh == null) throw new ArgumentNullException(nameof(mesh));

            var rng = new Random();
            const double sigma = 0.05; // 5% relative noise

            var elems = mesh.GetElements();
            var noisy = new Dictionary<int, double>(elems.Count);

            foreach (var el in elems)
            {
                // Box-Muller transform for normal distribution
                double u1 = 1.0 - rng.NextDouble();
                double u2 = 1.0 - rng.NextDouble();
                double z = Math.Sqrt(-2.0 * Math.Log(u1)) * Math.Cos(2.0 * Math.PI * u2);

                double value = el.Conductivity * (1.0 + sigma * z);
                if (value < 0) value = 0.0;
                el.Conductivity = value;
                noisy[el.Id] = value;
            }

            mesh.SetConductivityDistribution(new ConductivityDistribution(noisy));

            Workspace.AddLogMessage("MeshFactory", "Added gaussian noise to mesh conductivities.");
        }

        private static void ValidatePerimeter(IList<(double x, double y)> perimeter)
        {
            if (perimeter is null || perimeter.Count < 3)
                throw new ArgumentException("Perimeter must contain at least three points.");
            for (int i = 0; i < perimeter.Count; i++)
            {
                var a = perimeter[i];
                var b = perimeter[(i + 1) % perimeter.Count];
                if (a == b)
                    throw new ArgumentException("Consecutive perimeter points must be distinct.");
            }
        }

        private static bool IsPointInPolygon(double x, double y, IList<(double x, double y)> polygon)
        {
            bool inside = false;
            for (int i = 0, j = polygon.Count - 1; i < polygon.Count; j = i++)
            {
                var pi = polygon[i];
                var pj = polygon[j];
                bool intersect = ((pi.y > y) != (pj.y > y)) &&
                    (x < (pj.x - pi.x) * (y - pi.y) / ((pj.y - pi.y) + double.Epsilon) + pi.x);
                if (intersect)
                    inside = !inside;
            }
            return inside;
        }
    }
}
