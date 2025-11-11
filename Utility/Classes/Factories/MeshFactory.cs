using MIConvexHull;
using System.Xml.Linq;
using Utility.Classes.Application;
using Utility.Classes.Discretizer;
using Utility.Classes.Discretizer.FiniteElementMesh;
using Utility.Classes.Discretizer.LatticeBoltzmannGrid;

namespace Utility.Classes.Factories
{
    /// <summary>
    /// The mesh factory can be used to genreate Finite Element meshes and Lattice Boltzmann meshes
    /// TODO: add generic perimeter description based mesh generation
    /// </summary>
    public static class MeshFactory
    {
        private const double MinTriangleArea = 1e-10;
        private const double MinEdgeLengthSquared = 1e-12;

        public static IDiscretization Create(DiscretizationParameters parameters, double inhomogenityValue = 1.0) => parameters.MeshType switch
        {
            DiscretizationType.FEM => CreateCircularFEMMesh(layers: parameters.Layers,
                                                  boundaryFEMVertexCount: parameters.BoundaryFEMVertexCount,
                                                  electrodeCount: parameters.ElectrodeCount,
                                                  inhomogeneityValue: inhomogenityValue),
            DiscretizationType.LBM => LBMCreateCircular(parameters.Nx, parameters.Ny, parameters.Radius, parameters.ElectrodeCount),
            _ => throw new NotSupportedException()
        };

        public static IDiscretization CreateDefault(DiscretizationParameters parameters) => Create(parameters, 1.0);
        

        #region Finite Element Mesh Generation

        /// <summary>
        /// Builds a circular FEM mesh with given concentric layers and boundary vertices,
        /// then distributes `electrodeCount` electrodes evenly around the outer boundary.
        /// </summary>
        public static FEMMesh CreateCircularFEMMesh(int layers, int boundaryFEMVertexCount, int electrodeCount = 16, int nodesPerElectrode = 1, double electrodeLengthHint = 0.3)
        {
            var mesh = CreateCircularFEMMeshInternal(layers, boundaryFEMVertexCount, electrodeCount, inhomogeneityValue: 3.0, nodesPerElectrode: nodesPerElectrode, electrodeLengthHint: electrodeLengthHint);

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
        public static FEMMesh CreateCircularFEMMesh(int layers, int boundaryFEMVertexCount, int electrodeCount = 16, double inhomogeneityValue = 3.0, int nodesPerElectrode = 1, double electrodeLengthHint = 0.3)
        {
            var mesh = CreateCircularFEMMeshInternal(layers, boundaryFEMVertexCount, electrodeCount, inhomogeneityValue, nodesPerElectrode, electrodeLengthHint);

            mesh.Metadata.Generator = nameof(CreateCircularFEMMesh);
            mesh.Metadata.Parameters["layers"] = layers.ToString();
            mesh.Metadata.Parameters["boundaryFEMVertexCount"] = boundaryFEMVertexCount.ToString();
            mesh.Metadata.Parameters["electrodeCount"] = electrodeCount.ToString();
            mesh.Metadata.Parameters["inhomogeneityValue"] = inhomogeneityValue.ToString();

            Workspace.AddLogMessage("MeshFactory", "Created Ciruclar FEMMesh object");

            return mesh;
        }

        // common implementation with inhomogeneity scaling
        private static FEMMesh CreateCircularFEMMeshInternal(int layers, int boundaryFEMVertexCount, int electrodeCount, double inhomogeneityValue, int nodesPerElectrode, double electrodeLengthHint)
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

            // 2) build layered triangulation explicitly to avoid sliver elements
            var rings = new List<List<FEMVertex>>(layers + 1)
            {
                new List<FEMVertex> { vertices[0] } // center
            };

            for (int layer = 1; layer <= layers; layer++)
            {
                var ring = new List<FEMVertex>(boundaryFEMVertexCount);
                for (int i = 0; i < boundaryFEMVertexCount; i++)
                {
                    int index = 1 + (layer - 1) * boundaryFEMVertexCount + i;
                    ring.Add(vertices[index]);
                }
                rings.Add(ring);
            }

            var elements = new List<FEMElement>();
            int eid = 0;
            double innerRadius = 1.0 / (layers + 1e-4); // threshold

            if (layers >= 1 && boundaryFEMVertexCount >= 3)
            {
                var center = rings[0][0];
                var firstRing = rings[1];
                for (int i = 0; i < boundaryFEMVertexCount; i++)
                {
                    int next = (i + 1) % boundaryFEMVertexCount;
                    if (TryCreateElement(center, firstRing[i], firstRing[next], eid, innerRadius, inhomogeneityValue, out var element))
                    {
                        if (element is null)
                            throw new NullReferenceException();

                        elements.Add(element);
                        eid = element.Id + 1;
                    }
                }
            }

            for (int layer = 2; layer <= layers; layer++)
            {
                var innerRing = rings[layer - 1];
                var outerRing = rings[layer];

                for (int i = 0; i < boundaryFEMVertexCount; i++)
                {
                    int next = (i + 1) % boundaryFEMVertexCount;

                    if (TryCreateElement(innerRing[i], outerRing[i], outerRing[next], eid, innerRadius, inhomogeneityValue, out var first))
                    {
                        if (first is null)
                            throw new NullReferenceException();

                        elements.Add(first);
                        eid = first.Id + 1;
                    }

                    if (TryCreateElement(innerRing[i], outerRing[next], innerRing[next], eid, innerRadius, inhomogeneityValue, out var second))
                    {
                        if (second is null)
                            throw new NullReferenceException();

                        elements.Add(second);
                        eid = second.Id + 1;
                    }
                }
            }

            // 4) assemble mesh
            var mesh = new FEMMesh(vertices, elements);

            mesh.PlaceEquidistantElectrodes(electrodeCount, 0.1, electrodeLengthHint, nodesPerElectrode);

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
        /// The algorithm creates concentric scaled copies of the perimeter, distributes vertices
        /// across the layers and performs a Delaunay triangulation. Electrodes are placed on
        /// equally spaced boundary vertices. If electrodeCount exceeds the number of boundary
        /// vertices it is clamped accordingly.
        /// </summary>
        public static FEMMesh CreatePolygonFEMMesh(IList<(double x, double y)> perimeter, int layers, int electrodeCount = 16, int nodesPerElectrode = 1, double electrodeLengthHint = 0.3)
        {
            ValidatePerimeter(perimeter);

            var vertices = new List<FEMVertex>();
            int vid = 0;

            double cx = perimeter.Average(p => p.x);
            double cy = perimeter.Average(p => p.y);
            var center = new FEMVertex(vid++, cx, cy);
            vertices.Add(center);

            for (int layer = 1; layer <= layers; layer++)
            {
                double t = (double)layer / layers;
                for (int i = 0; i < perimeter.Count; i++)
                {
                    var p = perimeter[i];
                    double nx = cx + (p.x - cx) * t;
                    double ny = cy + (p.y - cy) * t;
                    vertices.Add(new FEMVertex(vid++, nx, ny)
                    {
                        IsBoundary = (layer == layers),
                        BoundaryId = (layer == layers ? i : -1)
                    });
                }
            }

            var triVerts = vertices.Select(v => new TriFEMVertex(v)).ToArray();
            var delaunay = DelaunayTriangulation<TriFEMVertex, DefaultTriangulationCell<TriFEMVertex>>.Create(triVerts, 1e-3);

            var elements = new List<FEMElement>();
            int eid = 0;
            foreach (var cell in delaunay.Cells)
            {
                var a = cell.Vertices[0].Original;
                var b = cell.Vertices[1].Original;
                var c = cell.Vertices[2].Original;

                double mx = (a.X + b.X + c.X) / 3.0;
                double my = (a.Y + b.Y + c.Y) / 3.0;
                if (!IsPointInPolygon(mx, my, perimeter))
                    continue;

                if (TryCreateElement(a, b, c, eid, out var element))
                {
                    if (element is null)
                        throw new NullReferenceException();

                    elements.Add(element);
                    eid = element.Id + 1;
                }
            }

            var mesh = new FEMMesh(vertices, elements);

            mesh.PlaceEquidistantElectrodes(electrodeCount, 0.1, electrodeLengthHint, nodesPerElectrode);

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
            mesh.Metadata.Parameters["layers"] = layers.ToString();

            return mesh;
        }

        /// <summary>
        /// Convenience wrapper that builds a rectangular FEM mesh from corner points.
        /// </summary>
        public static FEMMesh CreateRectangularFEMMesh(double width, double height, int electrodeCount = 16, int layers = 1, int nodesPerElectrode = 1, double electrodeLengthHint = 0.3)
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
            var mesh = CreatePolygonFEMMesh(pts, layers, electrodeCount, nodesPerElectrode, electrodeLengthHint);
            mesh.Metadata.Generator = nameof(CreateRectangularFEMMesh);
            mesh.Metadata.Parameters["width"] = width.ToString();
            mesh.Metadata.Parameters["height"] = height.ToString();
            mesh.Metadata.Parameters["electrodeCount"] = electrodeCount.ToString();
            mesh.Metadata.Parameters["layers"] = layers.ToString();
            return mesh;
        }

        /// <summary>
        /// Create a FEM mesh from an arbitrary thorax-shaped perimeter.
        /// </summary>
        public static FEMMesh CreateThoraxFEMMesh(IList<(double x, double y)> perimeter, int electrodeCount = 16, int layers = 1, int nodesPerElectrode = 1, double electrodeLengthHint = 0.3)
        {
            var mesh = CreatePolygonFEMMesh(perimeter, layers, electrodeCount, nodesPerElectrode, electrodeLengthHint);
            mesh.Metadata.Generator = nameof(CreateThoraxFEMMesh);
            mesh.Metadata.Parameters["electrodeCount"] = electrodeCount.ToString();
            mesh.Metadata.Parameters["perimeter"] = string.Join(";", perimeter.Select(p => $"{p.x},{p.y}"));
            mesh.Metadata.Parameters["layers"] = layers.ToString();
            return mesh;
        }

        private static bool TryCreateElement(FEMVertex a,
                                             FEMVertex b,
                                             FEMVertex c,
                                             int elementId,
                                             double innerRadius,
                                             double inhomogeneityValue,
                                             out FEMElement? element)
        {
            if (!TryCreateElement(a, b, c, elementId, out element))
                return false;

            if (element is null)
                throw new NullReferenceException();

            ApplyLayeredConductivity(element, innerRadius, inhomogeneityValue);
            return true;
        }

        private static bool TryCreateElement(FEMVertex a,
                                             FEMVertex b,
                                             FEMVertex c,
                                             int elementId,
                                             out FEMElement? element)
        {
            element = null;

            if (!IsTriangleValid(a, b, c))
                return false;

            element = new FEMElement(elementId, a, b, c);
            return true;
        }

        private static void ApplyLayeredConductivity(FEMElement element, double innerRadius, double inhomogeneityValue)
        {
            var verts = element.Vertices;
            double rA = Math.Sqrt(verts[0].X * verts[0].X + verts[0].Y * verts[0].Y);
            double rB = Math.Sqrt(verts[1].X * verts[1].X + verts[1].Y * verts[1].Y);
            double rC = Math.Sqrt(verts[2].X * verts[2].X + verts[2].Y * verts[2].Y);

            double avgRadius = (rA + rB + rC) / 3.0;
            element.Conductivity = avgRadius < innerRadius ? inhomogeneityValue : 1.0;
        }

        private static bool IsTriangleValid(FEMVertex a, FEMVertex b, FEMVertex c)
        {
            if (!double.IsFinite(a.X) || !double.IsFinite(a.Y) ||
                !double.IsFinite(b.X) || !double.IsFinite(b.Y) ||
                !double.IsFinite(c.X) || !double.IsFinite(c.Y))
            {
                return false;
            }

            if (HasTinyEdge(a, b) || HasTinyEdge(b, c) || HasTinyEdge(c, a))
                return false;

            double area = TriangleArea(a, b, c);
            return double.IsFinite(area) && area > MinTriangleArea;
        }

        private static double TriangleArea(FEMVertex a, FEMVertex b, FEMVertex c)
            => 0.5 * Math.Abs(a.X * (b.Y - c.Y)
                              + b.X * (c.Y - a.Y)
                              + c.X * (a.Y - b.Y));

        private static bool HasTinyEdge(FEMVertex a, FEMVertex b)
        {
            double dx = a.X - b.X;
            double dy = a.Y - b.Y;
            double lengthSquared = dx * dx + dy * dy;
            return lengthSquared < MinEdgeLengthSquared;
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
        public static LBMGrid CreateLBMGridFromPerimeter(int nx, int ny, IList<(double x, double y)> perimeter, int electrodeCount = 16)
        {
            ValidatePerimeter(perimeter);
            var grid = new LBMGrid(nx, ny);

            var inside = new bool[nx, ny];
            for (int y = 0; y < ny; y++)
                for (int x = 0; x < nx; x++)
                    inside[x, y] = IsPointInPolygon(x + 0.5, y + 0.5, perimeter);

            grid.ApplyDomainMask(inside);

            // place electrodes evenly around the domain boundary
            grid.PlaceEquidistantElectrodes(electrodeCount);

            var cd = grid.GetElements().ToDictionary(e => e.Id, e => e.Conductivity);
            grid.SetConductivityDistribution(new ConductivityDistribution(cd));

            Workspace.AddLogMessage("MeshFactory", "Created LBMGrid from Perimeter definition.");

            grid.Metadata.Generator = nameof(CreateLBMGridFromPerimeter);
            grid.Metadata.Parameters["nx"] = nx.ToString();
            grid.Metadata.Parameters["ny"] = ny.ToString();
            grid.Metadata.Parameters["electrodeCount"] = electrodeCount.ToString();
            grid.Metadata.Parameters["perimeter"] = string.Join(";", perimeter.Select(p => $"{p.x},{p.y}"));

            return grid;
        }

        public static LBMGrid CreateRectangularLBMGrid(int nx, int ny, int electrodeCount = 16)
        {
            var grid = new LBMGrid(nx, ny);

            grid.PlaceEquidistantElectrodes(electrodeCount);

            var cd = grid.GetElements().ToDictionary(e => e.Id, e => e.Conductivity);
            grid.SetConductivityDistribution(new ConductivityDistribution(cd));

            grid.Metadata.Generator = nameof(CreateRectangularLBMGrid);
            grid.Metadata.Parameters["nx"] = nx.ToString();
            grid.Metadata.Parameters["ny"] = ny.ToString();
            grid.Metadata.Parameters["electrodeCount"] = electrodeCount.ToString();
            return grid;
        }

        public static LBMGrid CreateThoraxLBMGrid(int nx, int ny, IList<(double x, double y)> perimeter, int electrodeCount = 16)
        {
            var grid = CreateLBMGridFromPerimeter(nx, ny, perimeter, electrodeCount);
            grid.Metadata.Generator = nameof(CreateThoraxLBMGrid);
            grid.Metadata.Parameters["nx"] = nx.ToString();
            grid.Metadata.Parameters["ny"] = ny.ToString();
            grid.Metadata.Parameters["electrodeCount"] = electrodeCount.ToString();
            grid.Metadata.Parameters["perimeter"] = string.Join(";", perimeter.Select(p => $"{p.x},{p.y}"));
            return grid;
        }

        private static LBMGrid LBMCreateRectangularWithBorder(int nx = 15, int ny = 15, int electrodeCount = 16)
        {
            var grid = new LBMGrid(nx, ny);
            grid.Metadata.Generator = nameof(LBMCreateRectangularWithBorder);
            grid.Metadata.Parameters["nx"] = nx.ToString();
            grid.Metadata.Parameters["ny"] = ny.ToString();
            grid.Metadata.Parameters["electrodeCount"] = electrodeCount.ToString();
            return grid;
        }


        private static LBMGrid LBMCreateRectangularWithInhomogenity(int nx = 15, int ny = 15, int electrodeCount = 16, double inhomogenityValue = 1.0, int inhomogenitySize = 4)
        {
            if (inhomogenitySize > nx || inhomogenitySize > ny)
                throw new ArgumentOutOfRangeException("Cannot create LBM mesh with inhomogenity, size too big!");

            LBMGrid grid = new LBMGrid(nx, ny);

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

                    var el = grid.GetElementAt(x, y);
                    el.Conductivity = inhomogenityValue;
                }
            }

            // 3) rebuild distribution so downstream code sees it
            var cd = grid.GetElements().ToDictionary(e => e.Id, e => e.Conductivity);
            grid.SetConductivityDistribution(new ConductivityDistribution(cd));

            grid.Metadata.Generator = nameof(LBMCreateRectangularWithInhomogenity);
            grid.Metadata.Parameters["nx"] = nx.ToString();
            grid.Metadata.Parameters["ny"] = ny.ToString();
            grid.Metadata.Parameters["electrodeCount"] = electrodeCount.ToString();
            grid.Metadata.Parameters["inhomogenityValue"] = inhomogenityValue.ToString();
            grid.Metadata.Parameters["inhomogenitySize"] = inhomogenitySize.ToString();
            return grid;
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
        private static LBMGrid LBMCreateCircular(int nx = 15, int ny = 15, int radius = 10, int electrodeCount = 16)
        {
            if (radius > nx / 2 || radius > ny / 2)
                throw new ArgumentOutOfRangeException(nameof(radius),
                    "Cannot create circular LBM mesh: radius too big.");

            var grid = new LBMGrid(nx, ny);

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

            var elements = grid.GetElements().Cast<LBMElement>();

            foreach (var el in elements)
            {
                el.IsElectrode = false;
                var (ex, ey) = grid.ToLattice(el.Id);
                double dx = ex - cx;
                double dy = ey - cy;
                double distSq = dx * dx + dy * dy;
                bool outside = distSq >= r2;
                bool onBoundary = circle.Contains((ex, ey));

                el.IsWall = outside || onBoundary || ex == 0 || ey == 0 || ex == nx - 1 || ey == ny - 1;
            }

            // Place electrodes on the wall cells directly adjacent to the fluid domain
            grid.PlaceEquidistantElectrodes(electrodeCount);

            // Refresh conductivity distribution
            var cd = grid.GetElements().ToDictionary(e => e.Id, e => e.Conductivity);
            grid.SetConductivityDistribution(new ConductivityDistribution(cd));

            Workspace.AddLogMessage("MeshFactory", "Created ciruclar LBMGrid object.");

            grid.Metadata.Parameters["nx"] = nx.ToString();
            grid.Metadata.Generator = nameof(LBMCreateCircular);
            grid.Metadata.Parameters["ny"] = ny.ToString();
            grid.Metadata.Parameters["radius"] = radius.ToString();
            grid.Metadata.Parameters["electrodeCount"] = electrodeCount.ToString();

            return grid;
        }


        #endregion

        /// <summary>
        /// Perturbs the conductivity values of every element in the
        /// discretization using zero-mean Gaussian noise with a 5% relative
        /// standard deviation.
        /// </summary>
        public static void AddGaussianNoise(IDiscretization discretization)
        {
            if (discretization == null) throw new ArgumentNullException(nameof(discretization));

            var rng = new Random();
            const double sigma = 0.05; // 5% relative noise

            var elems = discretization.GetElements();
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

            discretization.SetConductivityDistribution(new ConductivityDistribution(noisy));

            Workspace.AddLogMessage("MeshFactory", "Added gaussian noise to mesh conductivities.");
        }

        /// <summary>
        /// Validates that a polygonal perimeter contains at least three
        /// distinct consecutive points.
        /// </summary>
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

        /// <summary>
        /// Implements the ray casting test to determine whether a point lies
        /// inside a polygon.
        /// </summary>
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
