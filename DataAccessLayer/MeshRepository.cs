using System.Collections.Generic;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Xml.Linq;
using System.Linq;
using Utility.Classes;
using Utility.Classes.Discretizer;
using Utility.Classes.Discretizer.FiniteElementMesh;
using Utility.Classes.Discretizer.LatticeBoltzmannGrid;
using Utility.Classes.Factories;
using Utility.Classes.Measurement;
using Utility.Classes.Application;

namespace DataAccessLayer
{
    public class MeshRepository : IMeshRepository
    {
        private static string MeshDir => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                                                       "EITApplication", "Meshes");
        private const double StlMergeTolerance = 1e-9;

        public void SaveFEMMesh(FEMMesh mesh, string name)
        {
            if (mesh == null) throw new ArgumentNullException(nameof(mesh));
            if (string.IsNullOrWhiteSpace(name)) throw new ArgumentException("Name required.", nameof(name));

            // We capture the timestamp once so that the .eitmesh and .stl exports end up next to each
            // other with matching file names. This makes it obvious to the user that both files
            // represent the same logical mesh snapshot.
            var timestamp = DateTime.UtcNow;

            var doc = BuildFemDocument(mesh, name);
            SaveDocument(doc, name, "fem", timestamp);

            // Additionally persist the mesh as an ASCII STL file so it can be exchanged with other
            // tools that understand the standard triangulated-surface format.  The helper builds
            // a conventional STL file where every FEM element becomes a triangular facet lying in the
            // XY plane.
            SaveFemMeshAsStl(mesh, name, "fem", timestamp);
        }

        public void SaveLBMGrid(LBMGrid grid, string name)
        {
            if (grid == null) throw new ArgumentNullException(nameof(grid));
            if (string.IsNullOrWhiteSpace(name)) throw new ArgumentException("Name required.", nameof(name));

            var timestamp = DateTime.UtcNow;
            var doc = BuildLbmDocument(grid, name);
            SaveDocument(doc, name, "lbm", timestamp);
        }

        public MatlabExportResult ExportFemMeshForMatlab(FEMMesh mesh, string name, DrivePattern drivePattern, string modelType)
        {
            if (mesh == null) throw new ArgumentNullException(nameof(mesh));
            if (string.IsNullOrWhiteSpace(name)) throw new ArgumentException("Name required.", nameof(name));
            if (string.IsNullOrWhiteSpace(modelType)) throw new ArgumentException("Model type required.", nameof(modelType));

            var timestamp = DateTime.UtcNow;
            var stlVertexOrder = new List<int>(mesh.Vertices.Count);
            string stlPath = SaveFemMeshAsStl(mesh, name, "matlab", timestamp, stlVertexOrder);

            var matlabVertexOrder = ComputeMatlabVertexOrder(mesh);
            var matlabIndexByVertexId = new Dictionary<int, int>(matlabVertexOrder.Count);
            for (int i = 0; i < matlabVertexOrder.Count; i++)
            {
                matlabIndexByVertexId[matlabVertexOrder[i]] = i;
            }

            var vertexById = mesh.Vertices.ToDictionary(v => v.GlobalId);
            var electrodes = mesh.ElectrodesTyped
                .Where(e => e != null)
                .OrderBy(e => e.Id)
                .ToList();
            var electrodeVertices = new List<int>(electrodes.Count);
            var electrodeVertexCoordinates = new List<double[]?>(electrodes.Count);
            var matlabElectrodeVertexIds = new List<int>(electrodes.Count);
            foreach (var electrode in electrodes)
            {
                int? vertexId = null;

                if (electrode.FEMVertexIds != null && electrode.FEMVertexIds.Count > 0)
                {
                    foreach (var candidate in electrode.FEMVertexIds)
                    {
                        if (vertexById.TryGetValue(candidate, out var vertex))
                        {
                            vertexId = vertex.GlobalId;
                            break;
                        }
                    }
                }

                if (!vertexId.HasValue && electrode.MeshId >= 0 && vertexById.TryGetValue(electrode.MeshId, out var meshVertex))
                {
                    vertexId = meshVertex.GlobalId;
                }

                int globalId = vertexId ?? -1;
                electrodeVertices.Add(globalId);

                if (globalId >= 0 && vertexById.TryGetValue(globalId, out var coordinateVertex))
                {
                    electrodeVertexCoordinates.Add(new[] { coordinateVertex.X, coordinateVertex.Y });
                }
                else
                {
                    electrodeVertexCoordinates.Add(null);
                }

                if (globalId >= 0 && matlabIndexByVertexId.TryGetValue(globalId, out var matlabIndex))
                {
                    // Matlab exposes vertex indices using one-based numbering; mirror that so the
                    // exported IDs can be used without further adjustment when the STL is loaded.
                    matlabElectrodeVertexIds.Add(matlabIndex + 1);
                }
                else
                {
                    matlabElectrodeVertexIds.Add(-1);
                }
            }

            var strategy = DrivePatternStrategyProvider.GetStrategy(drivePattern);
            var drivePatternPairs = new List<int[]>();
            if (electrodes.Count > 0)
            {
                int cycleLength = Math.Max(1, strategy.GetCycleLength(electrodes.Count));
                for (int step = 0; step < cycleLength; step++)
                {
                    var pair = strategy.GetElectrodePair(electrodes.Count, step);
                    drivePatternPairs.Add(new[] { pair.Excitation, pair.Ground });
                }
            }

            var export = new
            {
                stlPath = Path.GetFileName(stlPath),
                modelType,
                //electrodeVertices,
                electrodeVertexCoordinates,
                matlabElectrodeVertexIds,
                drivePatternPairs,
                stlVertexOrder = matlabVertexOrder

            };

            string jsonFile = Path.ChangeExtension(stlPath, ".json")
                ?? throw new InvalidOperationException("Unable to determine Matlab export JSON path.");

            var json = JsonSerializer.Serialize(export, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(jsonFile, json);

            return new MatlabExportResult(stlPath, jsonFile);
        }

        public IEnumerable<DiscretizationInfo> GetDiscretizationInfos()
        {
            Directory.CreateDirectory(MeshDir);
            foreach (var file in Directory.GetFiles(MeshDir, "*.eitmesh"))
            {
                DiscretizationInfo? info = null;
                try
                {
                    var doc = XDocument.Load(file);
                    var root = doc.Root;
                    if (root == null) continue;
                    var name = root.Attribute("name")?.Value ?? Path.GetFileNameWithoutExtension(file);
                    var md = DeserializeMetadata(root.Element("Metadata"));
                    info = new DiscretizationInfo(name, file, md);
                }
                catch
                {
                    // ignore invalid files
                }

                if (info != null)
                {
                    yield return info;
                }
            }

            foreach (var file in Directory.GetFiles(MeshDir, "*.stl"))
            {
                var info = TryBuildStlDiscretizationInfo(file);
                if (info != null)
                    yield return info;
            }
        }

        public void DeleteMesh(string filePath)
        {
            if (string.IsNullOrWhiteSpace(filePath))
                throw new ArgumentException("File path required.", nameof(filePath));

            if (!File.Exists(filePath))
                return;

            File.Delete(filePath);

            if (string.Equals(Path.GetExtension(filePath), ".eitmesh", StringComparison.OrdinalIgnoreCase))
            {
                var stlFile = Path.ChangeExtension(filePath, ".stl");
                if (!string.IsNullOrEmpty(stlFile) && File.Exists(stlFile))
                    File.Delete(stlFile);
            }
        }

        public FEMMesh LoadFEMMesh(string filePath)
        {
            if (!File.Exists(filePath))
                throw new FileNotFoundException($"Mesh file not found: {filePath}");

            // Support loading meshes from the standard STL format as well as from the application's
            // native XML container.  The file extension is the most reliable discriminator between
            // the two encodings.
            if (string.Equals(Path.GetExtension(filePath), ".stl", StringComparison.OrdinalIgnoreCase))
            {
                return LoadFemMeshFromStl(filePath);
            }

            var doc = XDocument.Load(filePath);
            var root = doc.Root ?? throw new InvalidOperationException("Invalid mesh file.");

            var metadata = DeserializeMetadata(root.Element("Metadata"));
            FEMMesh mesh = CreateFemFromMetadata(metadata);

            // overwrite vertex data
            var verticesEl = root.Element("Vertices");
            if (verticesEl != null)
            {
                foreach (var vx in verticesEl.Elements("Vertex"))
                {
                    int id = (int)(vx.Attribute("id") ?? throw new NullReferenceException());
                    var v = mesh.Vertices.FirstOrDefault(v => v.GlobalId == id);
                    if (v != null)
                    {
                        v.X = (double)(vx.Attribute("x") ?? throw new NullReferenceException());
                        v.Y = (double)(vx.Attribute("y") ?? throw new NullReferenceException());
                        v.Potential = (double)(vx.Attribute("potential") ?? throw new NullReferenceException());
                        v.IsBoundary = (bool)(vx.Attribute("isBoundary") ?? throw new NullReferenceException());
                        v.IsElectrode = (bool)(vx.Attribute("isElectrode") ?? throw new NullReferenceException());
                        v.BoundaryId = (int)(vx.Attribute("boundaryId") ?? throw new NullReferenceException());
                        v.ElectrodeId = (int)(vx.Attribute("electrodeId") ?? throw new NullReferenceException());
                    }
                }
            }

            // elements
            var elementsEl = root.Element("Elements");
            if (elementsEl != null)
            {
                var vDict = mesh.Vertices.ToDictionary(v => v.GlobalId);
                var elems = new List<FEMElement>();
                foreach (var el in elementsEl.Elements("Element"))
                {
                    int id = (int)(el.Attribute("id") ?? throw new NullReferenceException());
                    int v1 = (int)(el.Attribute("v1") ?? throw new NullReferenceException());
                    int v2 = (int)(el.Attribute("v2") ?? throw new NullReferenceException());
                    int v3 = (int)(el.Attribute("v3") ?? throw new NullReferenceException());
                    double cond = (double)(el.Attribute("conductivity") ?? throw new NullReferenceException());
                    var femEl = new FEMElement(id, vDict[v1], vDict[v2], vDict[v3])
                    {
                        Conductivity = cond
                    };
                    elems.Add(femEl);
                }
                if (elems.Count > 0)
                    mesh.SetElements(elems);
            }

            mesh.Initialize();

            // electrodes
            var electrodes = root.Element("Electrodes")?.Elements("Electrode").Select(e =>
            {
                var el = new FEMElectrode(
                    id: (int)(e.Attribute("id") ?? throw new NullReferenceException()),
                    meshId: (int)(e.Attribute("meshId") ?? throw new NullReferenceException()),
                    current: (double)(e.Attribute("current") ?? throw new NullReferenceException()),
                    zContact: (double)(e.Attribute("zContact") ?? throw new NullReferenceException()),
                    voltage: (double)(e.Attribute("voltage") ?? throw new NullReferenceException()),
                    isExcitation: (bool)(e.Attribute("isExcitation") ?? throw new NullReferenceException()),
                    isGround: (bool)(e.Attribute("isGround") ?? throw new NullReferenceException()),
                    isMeasuring: (bool)(e.Attribute("isMeasuring") ?? throw new NullReferenceException()),
                    pointElectrode: (bool)(e.Attribute("pointElectrode") ?? throw new NullReferenceException())
                );
                var ids = e.Element("VertexIds")?.Elements("VertexId").Select(vx => (int)vx).ToList();
                if (ids != null && ids.Count > 0)
                    el.FEMVertexIds.AddRange(ids);
                return el;
            }).ToList() ?? new List<FEMElectrode>();
            if (electrodes.Count > 0)
            {
                mesh.SetElectrodes(electrodes);
                mesh.UpdateElectrodeLengths();
            }

            // distributions
            var cdDict = root.Element("ConductivityDistribution")?.Elements("Value")
                              .ToDictionary(v => (int)(v.Attribute("elementId") ?? throw new NullReferenceException()),
                                            v => (double)(v.Attribute("sigma") ?? throw new NullReferenceException()))
                          ?? new Dictionary<int, double>();
            mesh.SetConductivityDistribution(new ConductivityDistribution(cdDict));

            var pdDict = root.Element("PotentialDistribution")?.Elements("Value")
                              .ToDictionary(v => (int)(v.Attribute("id") ?? throw new NullReferenceException()), 
                                            v => (double)(v.Attribute("phi") ?? throw new NullReferenceException()))
                          ?? new Dictionary<int, double>();
            mesh.SetPotentialDistribution(new PotentialDistribution(pdDict));

            mesh.Metadata = metadata;
            return mesh;
        }

        public LBMGrid LoadLBMGrid(string filePath)
        {
            if (!File.Exists(filePath))
                throw new FileNotFoundException($"Mesh file not found: {filePath}");

            var doc = XDocument.Load(filePath);
            var root = doc.Root ?? throw new InvalidOperationException("Invalid mesh file.");

            var metadata = DeserializeMetadata(root.Element("Metadata"));
            LBMGrid grid = CreateLbmFromMetadata(metadata);

            // elements
            var elementsEl = root.Element("Elements");
            if (elementsEl != null)
            {
                foreach (var el in elementsEl.Elements("Element"))
                {
                    int id = (int)(el.Attribute("id") ?? throw new NullReferenceException());
                    var e = grid.ElementsTyped.FirstOrDefault(x => x.Id == id);
                    if (e != null)
                    {
                        e.Conductivity = (double)(el.Attribute("conductivity") ?? throw new NullReferenceException());
                        e.IsWall = (bool)(el.Attribute("isWall") ?? throw new NullReferenceException());
                        e.IsElectrode = (bool)(el.Attribute("isElectrode")  ?? throw new NullReferenceException());
                    }
                }
            }

            // electrodes
            var electrodes = root.Element("Electrodes")?.Elements("Electrode").Select(e =>
                new LBMElectrode(
                    id: (int)(e.Attribute("id") ?? throw new NullReferenceException()),
                    gridId: (int)(e.Attribute("gridId") ?? throw new NullReferenceException()),
                    current: (double)(e.Attribute("current") ?? throw new NullReferenceException()),
                    contactImpedance: (double)(e.Attribute("zContact") ?? throw new NullReferenceException()),
                    potential: (double)(e.Attribute("voltage") ?? throw new NullReferenceException()),
                    isExcitation: (bool)(e.Attribute("isExcitation") ?? throw new NullReferenceException()),
                    isGround: (bool)(e.Attribute("isGround") ?? throw new NullReferenceException()),
                    isMeasuring: (bool)(e.Attribute("isMeasuring") ?? throw new NullReferenceException()))
            ).ToList() ?? new List<LBMElectrode>();
            if (electrodes.Count > 0)
                grid.SetElectrodes(electrodes);

            var cdDict = root.Element("ConductivityDistribution")?.Elements("Value")
                              .ToDictionary(v => (int)((v.Attribute("elementId") ?? throw new NullReferenceException()) ?? throw new NullReferenceException()),
                                            v => (double)(v.Attribute("sigma") ?? throw new NullReferenceException()))
                          ?? new Dictionary<int, double>();
            grid.SetConductivityDistribution(new ConductivityDistribution(cdDict));

            var pdDict = root.Element("PotentialDistribution")?.Elements("Value")
                              .ToDictionary(v => (int)(v.Attribute("id") ?? throw new NullReferenceException()),
                                            v => (double)(v.Attribute("phi") ?? throw new NullReferenceException()))
                          ?? new Dictionary<int, double>();
            grid.SetPotentialDistribution(new PotentialDistribution(pdDict));

            grid.Metadata = metadata;
            return grid;
        }

        private static void SaveDocument(XDocument doc, string name, string suffix, DateTime timestamp, string extension = "eitmesh")
        {
            Directory.CreateDirectory(MeshDir);
            string safeName = string.Join("_", name.Split(Path.GetInvalidFileNameChars()));
            string file = Path.Combine(MeshDir, $"{safeName}_{timestamp:yyyyMMdd_HHmmss}_{suffix}.{extension}");
            doc.Save(file);
        }

        private static string SaveFemMeshAsStl(FEMMesh mesh, string name, string suffix, DateTime timestamp, IList<int>? stlVertexOrder = null)
        {
            Directory.CreateDirectory(MeshDir);
            string safeName = string.Join("_", name.Split(Path.GetInvalidFileNameChars()));
            string file = Path.Combine(MeshDir, $"{safeName}_{timestamp:yyyyMMdd_HHmmss}_{suffix}.stl");

            // STL is defined for 3D surfaces. Our FEM mesh is planar, therefore we embed it in the
            // XY plane (all vertices have Z = 0).  Each triangular FEM element is written as one
            // STL facet.  The ASCII flavour is deliberately chosen because it is human readable and
            // many downstream tools (for example meshing utilities) accept it directly.
            HashSet<int>? seenVertices = stlVertexOrder != null ? new HashSet<int>() : null;
            using var writer = new StreamWriter(file, false, new System.Text.UTF8Encoding(false));

            writer.WriteLine($"solid {safeName}");

            var elements = mesh.ElementsTyped
                .OrderBy(e => e.Id)
                .ToList();

            foreach (var element in elements)
            {
                var a = element.Vertices[0];
                var b = element.Vertices[1];
                var c = element.Vertices[2];

                var normal = CalculateFacetNormal(a, b, c);
                writer.WriteLine(string.Format(CultureInfo.InvariantCulture,
                    "  facet normal {0:G17} {1:G17} {2:G17}", normal.X, normal.Y, normal.Z));
                writer.WriteLine("    outer loop");
                WriteVertex(writer, a);
                WriteVertex(writer, b);
                WriteVertex(writer, c);
                writer.WriteLine("    endloop");
                writer.WriteLine("  endfacet");
            }

            writer.WriteLine("endsolid");

            static (double X, double Y, double Z) CalculateFacetNormal(FEMVertex a, FEMVertex b, FEMVertex c)
            {
                // Build two edge vectors in 3D (with Z = 0) and compute their cross product.  The
                // resulting vector is perpendicular to the facet; we normalise it so the STL file
                // contains unit-length normals.  Degenerate (zero-area) triangles fallback to the
                // default +Z normal so the STL viewer can still display them.
                double ux = b.X - a.X;
                double uy = b.Y - a.Y;
                double vx = c.X - a.X;
                double vy = c.Y - a.Y;

                double nx = 0.0;
                double ny = 0.0;
                double nz = ux * vy - uy * vx;

                double length = Math.Sqrt(nx * nx + ny * ny + nz * nz);
                if (length < 1e-12)
                    return (0.0, 0.0, 1.0);

                return (nx / length, ny / length, nz / length);
            }

            void WriteVertex(StreamWriter writer, FEMVertex vertex)
            {
                if (stlVertexOrder != null && seenVertices != null && seenVertices.Add(vertex.GlobalId))
                {
                    stlVertexOrder.Add(vertex.GlobalId);
                }

                writer.WriteLine(string.Format(CultureInfo.InvariantCulture,
                    "      vertex {0:G17} {1:G17} 0", vertex.X, vertex.Y));
            }

            return file;
        }

        private static List<int> ComputeMatlabVertexOrder(FEMMesh mesh)
        {
            if (mesh == null)
                throw new ArgumentNullException(nameof(mesh));

            var uniqueVertices = mesh.Vertices
                .Select(v => new { Vertex = v, Key = CreateVertexKey(v.X, v.Y, 0.0) })
                .GroupBy(v => v.Key)
                .Select(g => g.OrderBy(v => v.Vertex.GlobalId).First())
                .ToList();

            uniqueVertices.Sort((a, b) =>
            {
                int compare = a.Key.X.CompareTo(b.Key.X);
                if (compare != 0)
                    return compare;

                compare = a.Key.Y.CompareTo(b.Key.Y);
                if (compare != 0)
                    return compare;

                return a.Key.Z.CompareTo(b.Key.Z);
            });

            return uniqueVertices
                .Select(v => v.Vertex.GlobalId)
                .ToList();
        }

        private static XDocument BuildFemDocument(FEMMesh mesh, string name)
        {
            mesh.Metadata.ElementCount = mesh.ElementsTyped.Count;
            var doc = new XDocument(
                new XElement("FEMMesh",
                    new XAttribute("name", name),
                    SerializeMetadata(mesh.Metadata),
                    new XElement("Elements",
                        mesh.ElementsTyped.Select(e =>
                            new XElement("Element",
                                new XAttribute("id", e.Id),
                                new XAttribute("v1", e.Vertices[0].GlobalId),
                                new XAttribute("v2", e.Vertices[1].GlobalId),
                                new XAttribute("v3", e.Vertices[2].GlobalId),
                                new XAttribute("conductivity", e.Conductivity))
                        )
                    ),
                    new XElement("Electrodes",
                        mesh.ElectrodesTyped.Select(e =>
                            new XElement("Electrode",
                                new XAttribute("id", e.Id),
                                new XAttribute("meshId", e.MeshId),
                                new XAttribute("current", e.Current),
                                new XAttribute("zContact", e.ZContact),
                                new XAttribute("voltage", e.Potential),
                                new XAttribute("isExcitation", e.IsExcitation),
                                new XAttribute("isGround", e.IsGround),
                                new XAttribute("isMeasuring", e.IsMeasuring),
                                new XAttribute("pointElectrode", e.PointElectrode),
                                e.FEMVertexIds.Count > 0 ?
                                    new XElement("VertexIds", e.FEMVertexIds.Select(id => new XElement("VertexId", id))) : null
                            )
                        )
                    ),
                    new XElement("Vertices",
                        mesh.Vertices.Select(v =>
                            new XElement("Vertex",
                                new XAttribute("id", v.GlobalId),
                                new XAttribute("x", v.X),
                                new XAttribute("y", v.Y),
                                new XAttribute("potential", v.Potential),
                                new XAttribute("isBoundary", v.IsBoundary),
                                new XAttribute("isElectrode", v.IsElectrode),
                                new XAttribute("boundaryId", v.BoundaryId),
                                new XAttribute("electrodeId", v.ElectrodeId))
                        )
                    ),
                    new XElement("ConductivityDistribution",
                        mesh.ConductivityDistribution.Conductivities.Select(kv =>
                            new XElement("Value",
                                new XAttribute("elementId", kv.Key),
                                new XAttribute("sigma", kv.Value))
                        )
                    ),
                    new XElement("PotentialDistribution",
                        mesh.PotentialDistribution.Potentials.Select(kv =>
                            new XElement("Value",
                                new XAttribute("id", kv.Key),
                                new XAttribute("phi", kv.Value))
                        )
                    )
                )
            );
            return doc;
        }

        private static XDocument BuildLbmDocument(LBMGrid mesh, string name)
        {
            mesh.Metadata.ElementCount = mesh.ElementsTyped.Count;
            var doc = new XDocument(
                new XElement("LBMGrid",
                    new XAttribute("name", name),
                    new XAttribute("nx", mesh.Nx),
                    new XAttribute("ny", mesh.Ny),
                    SerializeMetadata(mesh.Metadata),
                    new XElement("Elements",
                        mesh.ElementsTyped.Select(e =>
                            new XElement("Element",
                                new XAttribute("id", e.Id),
                                new XAttribute("conductivity", e.Conductivity),
                                new XAttribute("isWall", e.IsWall),
                                new XAttribute("isElectrode", e.IsElectrode))
                        )
                    ),
                    new XElement("Electrodes",
                        mesh.ElectrodesTyped.Select(e =>
                            new XElement("Electrode",
                                new XAttribute("id", e.Id),
                                new XAttribute("gridId", e.GridId),
                                new XAttribute("current", e.Current),
                                new XAttribute("zContact", e.ZContact),
                                new XAttribute("voltage", e.Potential),
                                new XAttribute("isExcitation", e.IsExcitation),
                                new XAttribute("isGround", e.IsGround),
                                new XAttribute("isMeasuring", e.IsMeasuring)
                            )
                        )
                    ),
                    new XElement("ConductivityDistribution",
                        mesh.ConductivityDistribution.Conductivities.Select(kv =>
                            new XElement("Value",
                                new XAttribute("elementId", kv.Key),
                                new XAttribute("sigma", kv.Value))
                        )
                    ),
                    new XElement("PotentialDistribution",
                        mesh.PotentialDistribution.Potentials.Select(kv =>
                            new XElement("Value",
                                new XAttribute("id", kv.Key),
                                new XAttribute("phi", kv.Value))
                        )
                    )
                )
            );
            return doc;
        }

        private static XElement SerializeMetadata(DiscretizationMetaData metadata)
        {
            return new XElement("Metadata",
                new XElement("CreatedOn", metadata.CreatedOn.ToString("o")),
                new XElement("Generator", metadata.Generator),
                new XElement("ElementCount", metadata.ElementCount),
                new XElement("Parameters",
                    metadata.Parameters.Select(p =>
                        new XElement("Parameter",
                            new XAttribute("key", p.Key),
                            new XAttribute("value", p.Value)))
                )
            );
        }

        private static DiscretizationMetaData DeserializeMetadata(XElement? element)
        {
            var md = new DiscretizationMetaData();
            if (element == null) return md;

            md.CreatedOn = DateTime.Parse(element.Element("CreatedOn")?.Value ?? DateTime.UtcNow.ToString("o"));
            md.Generator = element.Element("Generator")?.Value ?? string.Empty;
            md.ElementCount = int.Parse(element.Element("ElementCount")?.Value ?? "0");
            var dict = new Dictionary<string, string>();
            var parms = element.Element("Parameters");
            if (parms != null)
            {
                foreach (var p in parms.Elements("Parameter"))
                    dict[(string)(p.Attribute("key") ?? throw new NullReferenceException())] = (string)(p.Attribute("value") ?? throw new NullReferenceException());
            }
            md.Parameters = dict;
            return md;
        }

        private static FEMMesh CreateFemFromMetadata(DiscretizationMetaData md)
        {
            try
            {
                string circular = nameof(MeshFactory.CreateCircularFEMMesh);
                if (md.Generator == circular)
                {
                    md.Parameters.TryGetValue("layers", out var layersStr);
                    md.Parameters.TryGetValue("boundaryFEMVertexCount", out var boundaryStr);
                    md.Parameters.TryGetValue("electrodeCount", out var elStr);
                    md.Parameters.TryGetValue("inhomogeneityValue", out var inhStr);
                    int layers = int.Parse(layersStr ?? "1");
                    int boundary = int.Parse(boundaryStr ?? "8");
                    int electrodes = int.Parse(elStr ?? "16");
                    double inh = double.Parse(inhStr ?? "3,0");
                    return MeshFactory.CreateCircularFEMMesh(layers, boundary, electrodes, inh);
                }
                string rectangular = nameof(MeshFactory.CreateRectangularFEMMesh);
                if (md.Generator == rectangular)
                {
                    md.Parameters.TryGetValue("width", out var wStr);
                    md.Parameters.TryGetValue("height", out var hStr);
                    md.Parameters.TryGetValue("electrodeCount", out var elStr);
                    md.Parameters.TryGetValue("layers", out var layersStr);
                    double width = double.Parse(wStr ?? "1");
                    double height = double.Parse(hStr ?? "1");
                    int electrodes = int.Parse(elStr ?? "16");
                    int layers = int.Parse(layersStr ?? "1");
                    return MeshFactory.CreateRectangularFEMMesh(width, height, electrodes, layers);
                }
                string polygon = nameof(MeshFactory.CreatePolygonFEMMesh);
                if (md.Generator == polygon || md.Generator == nameof(MeshFactory.CreateThoraxFEMMesh))
                {
                    md.Parameters.TryGetValue("perimeter", out var pStr);
                    md.Parameters.TryGetValue("electrodeCount", out var elStr);
                    md.Parameters.TryGetValue("layers", out var layersStr);
                    var points = (pStr ?? string.Empty).Split(';', StringSplitOptions.RemoveEmptyEntries)
                                    .Select(s => s.Split(','))
                                    .Select(parts => (double.Parse(parts[0]), double.Parse(parts[1])))
                                    .ToList();
                    int electrodes = int.Parse(elStr ?? "16");
                    int layers = int.Parse(layersStr ?? "1");
                    var mesh = MeshFactory.CreatePolygonFEMMesh(points, layers, electrodes);
                    if (md.Generator == nameof(MeshFactory.CreateThoraxFEMMesh))
                        mesh.Metadata.Generator = nameof(MeshFactory.CreateThoraxFEMMesh);
                    return mesh;
                }
            }
            catch
            {
            }
            return new FEMMesh();
        }

        private static DiscretizationInfo? TryBuildStlDiscretizationInfo(string filePath)
        {
            try
            {
                bool isAscii;
                using (var stream = File.OpenRead(filePath))
                {
                    isAscii = IsAsciiStl(stream);
                }

                int triangleCount = CountTrianglesInStl(filePath, isAscii);

                var metadata = new DiscretizationMetaData
                {
                    CreatedOn = File.GetCreationTimeUtc(filePath),
                    Generator = isAscii ? "STL (ASCII)" : "STL (Binary)",
                    ElementCount = triangleCount,
                    Parameters = new Dictionary<string, string>
                    {
                        ["format"] = isAscii ? "ascii" : "binary",
                        ["source"] = Path.GetFileName(filePath)
                    }
                };

                // Append an explicit suffix so the UI distinguishes STL snapshots from the native
                // .eitmesh saves that may share the same base filename.
                string displayName = Path.GetFileNameWithoutExtension(filePath) + " [STL]";
                return new DiscretizationInfo(displayName, filePath, metadata);
            }
            catch
            {
                return null;
            }
        }

        private static FEMMesh LoadFemMeshFromStl(string filePath)
        {
            bool isAscii;
            using (var stream = File.OpenRead(filePath))
            {
                isAscii = IsAsciiStl(stream);
            }

            FEMMesh mesh = isAscii
                ? LoadFemMeshFromAsciiStl(filePath)
                : LoadFemMeshFromBinaryStl(filePath);

            mesh.Metadata = new DiscretizationMetaData
            {
                CreatedOn = File.GetCreationTimeUtc(filePath),
                Generator = isAscii ? "STL (ASCII)" : "STL (Binary)",
                ElementCount = mesh.ElementsTyped.Count,
                Parameters = new Dictionary<string, string>
                {
                    ["format"] = isAscii ? "ascii" : "binary",
                    ["source"] = Path.GetFileName(filePath)
                }
            };

            // STL files do not carry electrode definitions; ensure the mesh exposes an empty list so
            // higher layers do not accidentally work with stale data.
            mesh.SetElectrodes(new List<FEMElectrode>());

            // Rebuild the distributions so that subsequent processing steps can access them without
            // having to special-case imported meshes.
            var conductivity = mesh.ElementsTyped.ToDictionary(e => e.Id, e => e.Conductivity);
            mesh.SetConductivityDistribution(new ConductivityDistribution(conductivity));

            var potentials = mesh.Vertices.ToDictionary(v => v.GlobalId, v => v.Potential);
            mesh.SetPotentialDistribution(new PotentialDistribution(potentials));

            Workspace.ClearImportedMeasurement();
            TryAugmentFemMeshFromMatlabJson(mesh, filePath);

            return mesh;
        }

        private static void TryAugmentFemMeshFromMatlabJson(FEMMesh mesh, string stlFilePath)
        {
            if (mesh is null)
                throw new ArgumentNullException(nameof(mesh));

            string? jsonPath = Path.ChangeExtension(stlFilePath, ".json");
            if (string.IsNullOrEmpty(jsonPath) || !File.Exists(jsonPath))
                return;

            MatlabImportDefinition? import = null;
            MatlabJsonSupplement? supplement = null;

            try
            {
                var json = File.ReadAllText(jsonPath);
                using var document = JsonDocument.Parse(json, new JsonDocumentOptions { AllowTrailingCommas = true });
                import = document.RootElement.Deserialize<MatlabImportDefinition>(new JsonSerializerOptions
                {
                    PropertyNameCaseInsensitive = true
                });

                supplement = ExtractMatlabSupplement(document.RootElement);
            }
            catch
            {
                return;
            }

            if (import == null)
                return;

            const double CoordinateTolerance = 2e-2;

            mesh.Metadata.Parameters ??= new Dictionary<string, string>();
            mesh.Metadata.Parameters["matlabJson"] = Path.GetFileName(jsonPath);
            mesh.Metadata.Parameters["electrodeCoordinateTolerance"] = CoordinateTolerance.ToString(CultureInfo.InvariantCulture);

            if (!string.IsNullOrWhiteSpace(import.ModelType))
                mesh.Metadata.Parameters["matlabModelType"] = import.ModelType!;
            if (!string.IsNullOrWhiteSpace(import.EidorsVersion))
                mesh.Metadata.Parameters["eidorsVersion"] = import.EidorsVersion!;

            if (import.DrivePatternPairs != null && import.DrivePatternPairs.Count > 0)
            {
                try
                {
                    mesh.Metadata.Parameters["drivePatternPairs"] = JsonSerializer.Serialize(import.DrivePatternPairs);
                }
                catch
                {
                }
            }

            if (import.DrivePatternPairAmps != null && import.DrivePatternPairAmps.Count > 0)
            {
                try
                {
                    mesh.Metadata.Parameters["drivePatternPairAmps"] = JsonSerializer.Serialize(import.DrivePatternPairAmps);
                }
                catch
                {
                }
            }

            if (supplement?.MeasurementFrames != null && supplement.MeasurementFrames.Count > 0)
            {
                mesh.Metadata.Parameters["matlabMeasurementFrameCount"] = supplement.MeasurementFrames.Count.ToString(CultureInfo.InvariantCulture);
                try
                {
                    var measurement = supplement.MeasurementAmplitude.HasValue
                        ? new EITMeasurement(supplement.MeasurementFrames, supplement.MeasurementAmplitude.Value)
                        : new EITMeasurement(supplement.MeasurementFrames);

                    string label = ResolveMeasurementLabel(import);
                    Workspace.SetImportedMeasurement(measurement, label);
                    mesh.Metadata.Parameters["matlabMeasurementSource"] = label;
                }
                catch
                {
                    Workspace.ClearImportedMeasurement();
                }
            }

            if ((import.ElectrodeZContact == null || import.ElectrodeZContact.Count == 0) && supplement?.ContactImpedances != null)
                import.ElectrodeZContact = supplement.ContactImpedances;

            var electrodeCoordinates = import.ElectrodeVertexCoordinates;
            if ((electrodeCoordinates == null || electrodeCoordinates.Count == 0) && supplement?.ElectrodeCoordinates != null)
                electrodeCoordinates = supplement.ElectrodeCoordinates;

            var vertices = mesh.Vertices;
            foreach (var vertex in vertices)
            {
                vertex.IsElectrode = false;
                vertex.ElectrodeId = -1;
            }

            var electrodes = new List<FEMElectrode>();
            var reassignedElectrodes = new List<int>();

            double meshMinX = vertices.Min(v => v.X);
            double meshMaxX = vertices.Max(v => v.X);
            double meshMinY = vertices.Min(v => v.Y);
            double meshMaxY = vertices.Max(v => v.Y);
            double meshRangeX = meshMaxX - meshMinX;
            double meshRangeY = meshMaxY - meshMinY;

            var transform = DetermineElectrodeTransform(vertices, electrodeCoordinates, meshMinX, meshMaxX, meshMinY, meshMaxY);
            if (!string.IsNullOrEmpty(transform.Description))
                mesh.Metadata.Parameters["electrodeTransform"] = transform.Description!;

            if (electrodeCoordinates != null && electrodeCoordinates.Count > 0)
            {
                for (int i = 0; i < electrodeCoordinates.Count; i++)
                {
                    var coords = electrodeCoordinates[i];
                    if (coords == null || coords.Length == 0)
                        continue;

                    var (targetX, targetY) = ProjectElectrodeCoordinate(coords, transform, meshMinX, meshRangeX, meshMinY, meshRangeY);

                    var vertex = FindNearestVertex(
                        vertices,
                        targetX,
                        targetY,
                        CoordinateTolerance,
                        out bool withinTolerance,
                        v => v.IsBoundary);

                    if (vertex == null)
                    {
                        vertex = FindNearestVertex(vertices, targetX, targetY, CoordinateTolerance, out withinTolerance, null);
                        if (vertex == null)
                            continue;
                    }

                    if (!withinTolerance)
                        reassignedElectrodes.Add(i);

                    var electrode = new FEMElectrode(i, vertex.GlobalId, 0.0,
                        GetListValue(import.ElectrodeZContact, i, 0.0), 0.0,
                        pointElectrode: true)
                    {
                        IsMeasuring = true
                    };

                    electrode.FEMVertexIds.Add(vertex.GlobalId);

                    vertex.IsElectrode = true;
                    vertex.ElectrodeId = electrode.Id;

                    electrodes.Add(electrode);
                }

                if (electrodes.Count > 0)
                {
                    mesh.SetElectrodes(electrodes);
                    mesh.UpdateElectrodeLengths();
                }
            }

            if (reassignedElectrodes.Count > 0)
                mesh.Metadata.Parameters["electrodeReassignmentIndices"] = string.Join(",", reassignedElectrodes);

            if (import.DrivePatternPairs != null && import.DrivePatternPairs.Count > 0 && electrodes.Count > 0)
            {
                var firstPair = import.DrivePatternPairs[0];
                if (firstPair != null && firstPair.Length >= 2)
                {
                    int excitationId = firstPair[0];
                    int groundId = firstPair[1];

                    var firstAmps = import.DrivePatternPairAmps != null && import.DrivePatternPairAmps.Count > 0
                        ? import.DrivePatternPairAmps[0]
                        : null;

                    if (excitationId >= 0 && excitationId < electrodes.Count)
                    {
                        var excitation = electrodes[excitationId];
                        excitation.IsExcitation = true;
                        excitation.IsMeasuring = false;
                        excitation.Current = firstAmps != null && firstAmps.Length > 0 ? firstAmps[0] : excitation.Current;
                    }

                    if (groundId >= 0 && groundId < electrodes.Count)
                    {
                        var ground = electrodes[groundId];
                        ground.IsGround = true;
                        ground.IsMeasuring = false;
                        ground.Current = firstAmps != null && firstAmps.Length > 1 ? firstAmps[1] : ground.Current;
                    }
                }
            }

            if (import.InhomogenousPhantomElemData != null && import.InhomogenousPhantomElemData.Count > 0)
            {
                var conductivity = new Dictionary<int, double>(mesh.ElementsTyped.Count);
                for (int i = 0; i < mesh.ElementsTyped.Count; i++)
                {
                    double sigma = GetListValue(import.InhomogenousPhantomElemData, i, 1.0);
                    var element = mesh.ElementsTyped[i];
                    element.Conductivity = sigma;
                    conductivity[element.Id] = sigma;
                }

                if (conductivity.Count == mesh.ElementsTyped.Count)
                    mesh.SetConductivityDistribution(new ConductivityDistribution(conductivity));
            }
        }

        private static (double X, double Y) ProjectElectrodeCoordinate(
            IReadOnlyList<double> coords,
            ElectrodeTransform transform,
            double meshMinX,
            double meshRangeX,
            double meshMinY,
            double meshRangeY)
        {
            double ExtractValue(int axis)
            {
                if (axis >= 0 && axis < coords.Count)
                {
                    double value = coords[axis];
                    if (double.IsFinite(value))
                        return value;
                }

                foreach (var value in coords)
                {
                    if (double.IsFinite(value))
                        return value;
                }

                return 0.0;
            }

            double rawX = ExtractValue(transform.AxisX);
            double rawY = ExtractValue(transform.AxisY);

            if (!transform.HasBounds)
                return (rawX, rawY);

            double normalizedX = Normalize(rawX, transform.SourceMinX, transform.SourceMaxX);
            if (transform.FlipX)
                normalizedX = 1.0 - normalizedX;

            double normalizedY = Normalize(rawY, transform.SourceMinY, transform.SourceMaxY);
            if (transform.FlipY)
                normalizedY = 1.0 - normalizedY;

            double mappedX = meshMinX + normalizedX * meshRangeX;
            double mappedY = meshMinY + normalizedY * meshRangeY;

            return (mappedX, mappedY);
        }

        private static ElectrodeTransform DetermineElectrodeTransform(
            IReadOnlyList<FEMVertex> vertices,
            IReadOnlyList<double[]>? electrodeCoordinates,
            double meshMinX,
            double meshMaxX,
            double meshMinY,
            double meshMaxY)
        {
            var defaultTransform = new ElectrodeTransform(0, 1, 0.0, 1.0, 0.0, 1.0, false, false, false, null);

            if (electrodeCoordinates == null || electrodeCoordinates.Count == 0)
                return defaultTransform;

            int dimension = electrodeCoordinates.Max(c => c?.Length ?? 0);
            if (dimension < 2)
                return defaultTransform;

            var boundaryVertices = vertices.Where(v => v.IsBoundary).ToList();
            if (boundaryVertices.Count == 0)
                boundaryVertices = vertices.ToList();

            var axisPairs = new List<(int X, int Y)>();
            for (int a = 0; a < dimension; a++)
                for (int b = a + 1; b < dimension; b++)
                    axisPairs.Add((a, b));

            if (axisPairs.Count == 0)
                axisPairs.Add((0, 1));

            double meshRangeX = meshMaxX - meshMinX;
            double meshRangeY = meshMaxY - meshMinY;

            double bestError = double.PositiveInfinity;
            ElectrodeTransform bestTransform = defaultTransform;

            foreach (var (axisX, axisY) in axisPairs)
            {
                var (minX, maxX, hasBoundsX) = GetAxisBounds(electrodeCoordinates, axisX);
                var (minY, maxY, hasBoundsY) = GetAxisBounds(electrodeCoordinates, axisY);

                bool hasBounds = hasBoundsX && hasBoundsY;

                if (hasBounds)
                {
                    foreach (bool flipX in new[] { false, true })
                    foreach (bool flipY in new[] { false, true })
                    {
                        string description = $"axes[{axisX},{axisY}] {(flipX ? "invX" : "x")}/{(flipY ? "invY" : "y")}";
                        var transform = new ElectrodeTransform(axisX, axisY, minX, maxX, minY, maxY, flipX, flipY, true, description);
                        double error = ComputeMappingError(electrodeCoordinates, boundaryVertices, transform,
                            meshMinX, meshRangeX, meshMinY, meshRangeY);
                        if (error < bestError)
                        {
                            bestError = error;
                            bestTransform = transform;
                        }
                    }
                }
                else
                {
                    var transform = new ElectrodeTransform(axisX, axisY, minX, maxX, minY, maxY, false, false, false,
                        $"axes[{axisX},{axisY}] raw");
                    double error = ComputeMappingError(electrodeCoordinates, boundaryVertices, transform,
                        meshMinX, meshRangeX, meshMinY, meshRangeY);
                    if (error < bestError)
                    {
                        bestError = error;
                        bestTransform = transform;
                    }
                }
            }

            return bestTransform;
        }

        private static double ComputeMappingError(
            IReadOnlyList<double[]> coordinates,
            IReadOnlyList<FEMVertex> searchVertices,
            ElectrodeTransform transform,
            double meshMinX,
            double meshRangeX,
            double meshMinY,
            double meshRangeY)
        {
            if (coordinates.Count == 0 || searchVertices.Count == 0)
                return double.PositiveInfinity;

            double total = 0.0;
            int count = 0;

            foreach (var entry in coordinates)
            {
                if (entry == null || entry.Length == 0)
                    continue;

                var (mappedX, mappedY) = ProjectElectrodeCoordinate(entry, transform, meshMinX, meshRangeX, meshMinY, meshRangeY);

                var vertex = FindNearestVertex(searchVertices, mappedX, mappedY, 0.0, out _, null);
                if (vertex == null)
                    continue;

                double dx = vertex.X - mappedX;
                double dy = vertex.Y - mappedY;
                total += dx * dx + dy * dy;
                count++;
            }

            if (count == 0)
                return double.PositiveInfinity;

            return total / count;
        }

        private static (double Min, double Max, bool HasBounds) GetAxisBounds(
            IReadOnlyList<double[]> coordinates,
            int axis)
        {
            double min = double.PositiveInfinity;
            double max = double.NegativeInfinity;
            bool hasBounds = false;

            foreach (var entry in coordinates)
            {
                if (entry == null || axis < 0 || axis >= entry.Length)
                    continue;

                double value = entry[axis];
                if (!double.IsFinite(value))
                    continue;

                hasBounds = true;
                if (value < min) min = value;
                if (value > max) max = value;
            }

            return (min, max, hasBounds);
        }

        private static double Normalize(double value, double min, double max)
        {
            double range = max - min;
            if (!double.IsFinite(value) || range <= 1e-12)
                return 0.5;

            double normalized = (value - min) / range;
            if (!double.IsFinite(normalized))
                return 0.5;

            return Math.Clamp(normalized, 0.0, 1.0);
        }

        private static FEMVertex? FindNearestVertex(
            IReadOnlyList<FEMVertex> vertices,
            double x,
            double y,
            double tolerance,
            out bool withinTolerance,
            Func<FEMVertex, bool>? predicate = null)
        {
            static (FEMVertex? Vertex, double DistanceSquared) FindNearest(
                IReadOnlyList<FEMVertex> source,
                double targetX,
                double targetY,
                Func<FEMVertex, bool>? filter)
            {
                FEMVertex? candidate = null;
                double best = double.MaxValue;

                foreach (var vertex in source)
                {
                    if (filter != null && !filter(vertex))
                        continue;

                    double dx = vertex.X - targetX;
                    double dy = vertex.Y - targetY;
                    double distance = dx * dx + dy * dy;

                    if (distance < best)
                    {
                        best = distance;
                        candidate = vertex;
                    }
                }

                return (candidate, best);
            }

            var (best, bestDistance) = FindNearest(vertices, x, y, predicate);

            if (best == null && predicate != null)
            {
                (best, bestDistance) = FindNearest(vertices, x, y, null);
            }

            if (best == null)
            {
                withinTolerance = false;
                return null;
            }

            if (tolerance <= 0.0)
            {
                withinTolerance = true;
                return best;
            }

            withinTolerance = bestDistance <= tolerance * tolerance;

            return best;
        }

        private static double GetListValue(IReadOnlyList<double>? values, int index, double fallback)
        {
            if (values == null || index < 0 || index >= values.Count)
                return fallback;
            return values[index];
        }

        private static MatlabJsonSupplement? ExtractMatlabSupplement(JsonElement root)
        {
            try
            {
                var nodes = ExtractNodeCoordinates(root);
                var electrodeData = ExtractElectrodeData(root, nodes);
                var measurement = ExtractMeasurementData(root);

                return new MatlabJsonSupplement
                {
                    ElectrodeCoordinates = electrodeData.Coordinates,
                    ContactImpedances = electrodeData.ZContact,
                    MeasurementFrames = measurement.Frames,
                    MeasurementAmplitude = measurement.Amplitude
                };
            }
            catch
            {
                return null;
            }
        }

        private static List<double[]>? ParseElectrodeCoordinateArray(JsonElement element)
        {
            if (element.ValueKind != JsonValueKind.Array)
                return null;

            var coordinates = new List<double[]>();

            foreach (var entry in element.EnumerateArray())
            {
                var parsed = ParseElectrodeCoordinateEntry(entry);
                if (parsed != null && parsed.Length > 0)
                    coordinates.Add(parsed);
            }

            return coordinates.Count > 0 ? coordinates : null;
        }

        private static double[]? ParseElectrodeCoordinateEntry(JsonElement element)
        {
            if (element.ValueKind == JsonValueKind.Object)
            {
                var centre = ExtractDoubleArray(element, "centre") ?? ExtractDoubleArray(element, "center");
                if (centre != null && centre.Length > 0)
                    return centre;

                return null;
            }

            if (element.ValueKind != JsonValueKind.Array)
            {
                if (TryReadDouble(element, out double scalar))
                    return new[] { scalar };

                return null;
            }

            if (element.GetArrayLength() == 0)
                return Array.Empty<double>();

            var enumerator = element.EnumerateArray();
            if (!enumerator.MoveNext())
                return Array.Empty<double>();

            var first = enumerator.Current;

            if (first.ValueKind == JsonValueKind.Number || first.ValueKind == JsonValueKind.String)
                return ReadDoubleArray(element);

            if (first.ValueKind != JsonValueKind.Array)
                return ReadDoubleArray(element);

            var samples = new List<double[]>();
            foreach (var component in element.EnumerateArray())
            {
                var values = ReadDoubleArray(component);
                if (values != null && values.Length > 0)
                    samples.Add(values);
            }

            if (samples.Count == 0)
                return null;

            int dimension = samples.Max(s => s.Length);
            if (dimension == 0)
                return null;

            var totals = new double[dimension];
            var counts = new int[dimension];

            foreach (var sample in samples)
            {
                for (int i = 0; i < sample.Length; i++)
                {
                    totals[i] += sample[i];
                    counts[i]++;
                }
            }

            for (int i = 0; i < dimension; i++)
            {
                if (counts[i] > 0)
                    totals[i] /= counts[i];
            }

            return totals;
        }

        private static List<double[]>? ExtractNodeCoordinates(JsonElement root)
        {
            if (!TryFindPropertyCaseInsensitive(root, "nodes", out var nodesElement))
                return null;

            if (nodesElement.ValueKind != JsonValueKind.Array)
                return null;

            var nodes = new List<double[]>();
            foreach (var node in nodesElement.EnumerateArray())
            {
                if (node.ValueKind != JsonValueKind.Array)
                    continue;

                var coords = new List<double>();
                foreach (var component in node.EnumerateArray())
                {
                    if (TryReadDouble(component, out double value))
                        coords.Add(value);
                }

                if (coords.Count > 0)
                    nodes.Add(coords.ToArray());
            }

            return nodes.Count > 0 ? nodes : null;
        }

        private static (List<double[]>? Coordinates, List<double>? ZContact) ExtractElectrodeData(JsonElement root, List<double[]>? nodes)
        {
            if (!TryFindPropertyCaseInsensitive(root, "electrode", out var electrodeElement))
            {
                if (!TryFindPropertyCaseInsensitive(root, "electrodes", out electrodeElement))
                    return (null, null);
            }

            if (electrodeElement.ValueKind != JsonValueKind.Array)
                return (null, null);

            var coordinates = new List<double[]>();
            List<double>? zContacts = null;

            foreach (var electrode in electrodeElement.EnumerateArray())
            {
                double[]? coord = null;

                var indices = ExtractIntArray(electrode, "nodes")
                    ?? ExtractIntArray(electrode, "node_indices")
                    ?? ExtractIntArray(electrode, "nodeNumbers");

                if (indices != null && nodes != null && nodes.Count > 0)
                {
                    var positions = new List<double[]>();
                    foreach (int idx in indices)
                    {
                        var position = ResolveNode(nodes, idx);
                        if (position != null)
                            positions.Add(position);
                    }

                    if (positions.Count > 0)
                    {
                        int dimension = positions.Max(p => p.Length);
                        var averages = new double[dimension];
                        foreach (var pos in positions)
                        {
                            for (int i = 0; i < dimension && i < pos.Length; i++)
                                averages[i] += pos[i];
                        }

                        for (int i = 0; i < dimension; i++)
                            averages[i] /= positions.Count;

                        coord = averages;
                    }
                }

                if (coord == null)
                {
                    var centre = ExtractDoubleArray(electrode, "centre") ?? ExtractDoubleArray(electrode, "center");
                    if (centre != null && centre.Length > 0)
                        coord = centre;
                }

                if (coord != null)
                    coordinates.Add(coord);

                double? z = ExtractDouble(electrode, "z_contact");
                if (z.HasValue)
                {
                    zContacts ??= new List<double>();
                    zContacts.Add(z.Value);
                }
                else if (zContacts != null)
                {
                    zContacts.Add(0.0);
                }
            }

            if (zContacts != null && zContacts.Count < coordinates.Count)
            {
                while (zContacts.Count < coordinates.Count)
                    zContacts.Add(0.0);
            }

            return (coordinates.Count > 0 ? coordinates : null, zContacts);
        }

        private static (List<double[]>? Frames, double? Amplitude) ExtractMeasurementData(JsonElement root)
        {
            if (!TryFindPropertyCaseInsensitive(root, "vv", out var vvElement))
                return (null, null);

            if (vvElement.ValueKind != JsonValueKind.Array)
                return (null, null);

            var frames = new List<double[]>();
            foreach (var row in vvElement.EnumerateArray())
            {
                if (row.ValueKind != JsonValueKind.Array)
                    continue;

                var values = new List<double>();
                foreach (var entry in row.EnumerateArray())
                {
                    if (TryReadDouble(entry, out double value))
                        values.Add(value);
                }

                if (values.Count > 0)
                    frames.Add(values.ToArray());
            }

            return (frames.Count > 0 ? frames : null, null);
        }

        private static bool TryFindPropertyCaseInsensitive(JsonElement element, string name, out JsonElement value)
        {
            if (element.ValueKind == JsonValueKind.Object)
            {
                foreach (var property in element.EnumerateObject())
                {
                    if (string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase))
                    {
                        value = property.Value;
                        return true;
                    }

                    if (TryFindPropertyCaseInsensitive(property.Value, name, out value))
                        return true;
                }
            }
            else if (element.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in element.EnumerateArray())
                {
                    if (TryFindPropertyCaseInsensitive(item, name, out value))
                        return true;
                }
            }

            value = default;
            return false;
        }

        private static bool TryGetPropertyCaseInsensitive(JsonElement element, string name, out JsonElement value)
        {
            if (element.ValueKind == JsonValueKind.Object)
            {
                foreach (var property in element.EnumerateObject())
                {
                    if (string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase))
                    {
                        value = property.Value;
                        return true;
                    }
                }
            }

            value = default;
            return false;
        }

        private static List<int>? ExtractIntArray(JsonElement element, string propertyName)
        {
            if (!TryGetPropertyCaseInsensitive(element, propertyName, out var array) || array.ValueKind != JsonValueKind.Array)
                return null;

            var values = new List<int>();
            foreach (var item in array.EnumerateArray())
            {
                if (item.ValueKind == JsonValueKind.Number && item.TryGetInt32(out int intValue))
                    values.Add(intValue);
                else if (item.ValueKind == JsonValueKind.String && int.TryParse(item.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out intValue))
                    values.Add(intValue);
            }

            return values.Count > 0 ? values : null;
        }

        private static double[]? ExtractDoubleArray(JsonElement element, string propertyName)
        {
            if (!TryGetPropertyCaseInsensitive(element, propertyName, out var array) || array.ValueKind != JsonValueKind.Array)
                return null;

            return ReadDoubleArray(array);
        }

        private static double? ExtractDouble(JsonElement element, string propertyName)
        {
            if (!TryGetPropertyCaseInsensitive(element, propertyName, out var value))
                return null;

            return TryReadDouble(value, out double result) ? result : null;
        }

        private static double[]? ReadDoubleArray(JsonElement array)
        {
            if (array.ValueKind != JsonValueKind.Array)
                return null;

            var values = new List<double>();
            foreach (var item in array.EnumerateArray())
            {
                if (TryReadDouble(item, out double value))
                    values.Add(value);
            }

            return values.Count > 0 ? values.ToArray() : null;
        }

        private static bool TryReadDouble(JsonElement element, out double value)
        {
            switch (element.ValueKind)
            {
                case JsonValueKind.Number when element.TryGetDouble(out double numeric):
                    value = numeric;
                    return true;
                case JsonValueKind.String:
                    return double.TryParse(element.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out value);
                default:
                    value = 0.0;
                    return false;
            }
        }

        private static double[]? ResolveNode(List<double[]> nodes, int index)
        {
            if (nodes.Count == 0)
                return null;

            if (index >= 0 && index < nodes.Count)
                return nodes[index];

            int oneBased = index - 1;
            if (oneBased >= 0 && oneBased < nodes.Count)
                return nodes[oneBased];

            return null;
        }

        private static string ResolveMeasurementLabel(MatlabImportDefinition import)
        {
            if (!string.IsNullOrWhiteSpace(import.ModelType))
                return import.ModelType!;

            if (!string.IsNullOrWhiteSpace(import.EidorsVersion))
                return $"EIDORS {import.EidorsVersion}";

            return "Matlab";
        }

        private readonly struct ElectrodeTransform
        {
            public ElectrodeTransform(int axisX, int axisY, double sourceMinX, double sourceMaxX, double sourceMinY, double sourceMaxY, bool flipX, bool flipY, bool hasBounds, string? description)
            {
                AxisX = axisX;
                AxisY = axisY;
                SourceMinX = sourceMinX;
                SourceMaxX = sourceMaxX;
                SourceMinY = sourceMinY;
                SourceMaxY = sourceMaxY;
                FlipX = flipX;
                FlipY = flipY;
                HasBounds = hasBounds;
                Description = description;
            }

            public int AxisX { get; }
            public int AxisY { get; }
            public double SourceMinX { get; }
            public double SourceMaxX { get; }
            public double SourceMinY { get; }
            public double SourceMaxY { get; }
            public bool FlipX { get; }
            public bool FlipY { get; }
            public bool HasBounds { get; }
            public string? Description { get; }
        }

        private sealed class MatlabJsonSupplement
        {
            public List<double[]>? ElectrodeCoordinates { get; init; }
            public List<double>? ContactImpedances { get; init; }
            public List<double[]>? MeasurementFrames { get; init; }
            public double? MeasurementAmplitude { get; init; }
        }

        private sealed class MatlabImportDefinition
        {
            public string? StlPath { get; set; }
            public string? ModelType { get; set; }
            public string? EidorsVersion { get; set; }
            [JsonPropertyName("electrodeVertexCoordinates")]
            public JsonElement ElectrodeVertexCoordinatesElement { get; set; }
            [JsonIgnore]
            public List<double[]>? ElectrodeVertexCoordinates => ParseElectrodeCoordinateArray(ElectrodeVertexCoordinatesElement);
            public List<double>? ElectrodeZContact { get; set; }
            public List<int[]>? DrivePatternPairs { get; set; }
            public List<double[]>? DrivePatternPairAmps { get; set; }
            public List<double>? InhomogenousPhantomElemData { get; set; }
        }

        private static FEMMesh LoadFemMeshFromAsciiStl(string filePath)
        {
            var vertices = new List<(double X, double Y, double Z)>();
            var vertexLookup = new Dictionary<VertexKey, int>();
            var triangles = new List<(int A, int B, int C)>();

            using var reader = new StreamReader(filePath);
            var currentTriangle = new List<int>(3);
            string? line;
            while ((line = reader.ReadLine()) != null)
            {
                line = line.Trim();
                if (line.Length == 0)
                    continue;

                if (line.StartsWith("vertex", StringComparison.OrdinalIgnoreCase))
                {
                    var parts = line.Split((char[])null, StringSplitOptions.RemoveEmptyEntries);
                    if (parts.Length < 4)
                        throw new InvalidDataException($"Invalid STL vertex definition: '{line}'.");

                    double x = double.Parse(parts[1], CultureInfo.InvariantCulture);
                    double y = double.Parse(parts[2], CultureInfo.InvariantCulture);
                    double z = double.Parse(parts[3], CultureInfo.InvariantCulture);

                    int index = GetOrAddVertex(vertexLookup, vertices, x, y, z);
                    currentTriangle.Add(index);

                    // Each facet contributes exactly three vertex lines. Once collected, we store the
                    // triangle and start with a fresh accumulator.
                    if (currentTriangle.Count == 3)
                    {
                        triangles.Add((currentTriangle[0], currentTriangle[1], currentTriangle[2]));
                        currentTriangle.Clear();
                    }
                }
            }

            if (triangles.Count == 0)
                throw new InvalidDataException("The STL file does not contain any facets.");

            return CreateMeshFromTriangleSoup(vertices, triangles);
        }

        private static FEMMesh LoadFemMeshFromBinaryStl(string filePath)
        {
            var vertices = new List<(double X, double Y, double Z)>();
            var vertexLookup = new Dictionary<VertexKey, int>();
            var triangles = new List<(int A, int B, int C)>();

            using var stream = File.OpenRead(filePath);
            using var reader = new BinaryReader(stream);

            // Binary STL layout: 80 byte header, 4 byte triangle count, followed by triangle records.
            if (reader.BaseStream.Length < 84)
                throw new InvalidDataException("Binary STL file is too small to contain header information.");

            reader.ReadBytes(80); // header (ignored)
            uint triCount = reader.ReadUInt32();

            for (uint i = 0; i < triCount; i++)
            {
                if (reader.BaseStream.Position + 50 > reader.BaseStream.Length)
                    throw new InvalidDataException("Binary STL file is truncated while reading facets.");

                // Normal vector (3 floats) – we do not use it for FEM reconstruction but have to
                // advance the stream position.
                reader.ReadSingle();
                reader.ReadSingle();
                reader.ReadSingle();

                int[] indices = new int[3];
                for (int v = 0; v < 3; v++)
                {
                    double x = reader.ReadSingle();
                    double y = reader.ReadSingle();
                    double z = reader.ReadSingle();
                    indices[v] = GetOrAddVertex(vertexLookup, vertices, x, y, z);
                }

                triangles.Add((indices[0], indices[1], indices[2]));

                // Attribute byte count – typically zero, but the specification reserves two bytes.
                reader.ReadUInt16();
            }

            if (triangles.Count == 0)
                throw new InvalidDataException("The STL file does not contain any facets.");

            return CreateMeshFromTriangleSoup(vertices, triangles);
        }

        private static FEMMesh CreateMeshFromTriangleSoup(List<(double X, double Y, double Z)> vertexPositions,
                                                          List<(int A, int B, int C)> triangles)
        {
            if (vertexPositions.Count == 0)
                throw new InvalidDataException("STL file does not define any vertices.");

            var femVertices = new List<FEMVertex>(vertexPositions.Count);
            for (int i = 0; i < vertexPositions.Count; i++)
            {
                var (x, y, _) = vertexPositions[i];
                femVertices.Add(new FEMVertex
                {
                    GlobalId = i,
                    X = x,
                    Y = y,
                    BoundaryId = -1,
                    ElectrodeId = -1,
                    IsBoundary = false,
                    IsElectrode = false,
                    Potential = 0.0
                });
            }

            MarkBoundaryVertices(femVertices, triangles);

            var femElements = new List<FEMElement>(triangles.Count);
            for (int i = 0; i < triangles.Count; i++)
            {
                var (a, b, c) = triangles[i];
                if (a >= femVertices.Count || b >= femVertices.Count || c >= femVertices.Count)
                    throw new InvalidDataException("STL facet references an undefined vertex index.");

                var element = new FEMElement(i, femVertices[a], femVertices[b], femVertices[c])
                {
                    Conductivity = 1.0
                };
                femElements.Add(element);
            }

            var mesh = new FEMMesh(femVertices, femElements);
            return mesh;
        }

        private static void MarkBoundaryVertices(
            List<FEMVertex> vertices,
            List<(int A, int B, int C)> triangles)
        {
            if (vertices.Count == 0 || triangles.Count == 0)
                return;

            var edgeUses = new Dictionary<(int Min, int Max), BoundaryEdgeInfo>();

            void RegisterEdge(int from, int to)
            {
                var key = from < to ? (from, to) : (to, from);
                if (!edgeUses.TryGetValue(key, out var info))
                {
                    info = new BoundaryEdgeInfo();
                    edgeUses[key] = info;
                }

                info.Count++;
                if (info.Count == 1)
                {
                    info.Orientation = (from, to);
                }
            }

            foreach (var (a, b, c) in triangles)
            {
                RegisterEdge(a, b);
                RegisterEdge(b, c);
                RegisterEdge(c, a);
            }

            var boundaryVertexIndices = new HashSet<int>();

            foreach (var kvp in edgeUses)
            {
                var info = kvp.Value;
                if (info.Count == 1)
                {
                    boundaryVertexIndices.Add(info.Orientation.From);
                    boundaryVertexIndices.Add(info.Orientation.To);
                }
            }

            if (boundaryVertexIndices.Count == 0)
                return;

            foreach (int idx in boundaryVertexIndices)
            {
                if (idx >= 0 && idx < vertices.Count)
                {
                    var vertex = vertices[idx];
                    vertex.IsBoundary = true;
                }
            }

            var boundaryVertices = boundaryVertexIndices
                .Where(i => i >= 0 && i < vertices.Count)
                .Select(i => vertices[i])
                .ToList();

            if (boundaryVertices.Count == 0)
                return;

            double cx = boundaryVertices.Average(v => v.X);
            double cy = boundaryVertices.Average(v => v.Y);

            var ordered = boundaryVertices
                .OrderBy(v => Math.Atan2(v.Y - cy, v.X - cx))
                .ToList();

            for (int i = 0; i < ordered.Count; i++)
            {
                ordered[i].BoundaryId = i + 1;
            }
        }

        private sealed class BoundaryEdgeInfo
        {
            public int Count { get; set; }
            public (int From, int To) Orientation { get; set; }
        }

        private static bool IsAsciiStl(Stream stream)
        {
            if (!stream.CanSeek)
                throw new ArgumentException("Stream must support seeking for STL detection.", nameof(stream));

            long originalPosition = stream.Position;
            int headerLength = (int)Math.Min(1024, stream.Length - originalPosition);
            byte[] buffer = new byte[headerLength];
            stream.Read(buffer, 0, headerLength);
            stream.Position = originalPosition;

            string header = System.Text.Encoding.ASCII.GetString(buffer);

            if (!header.StartsWith("solid", StringComparison.OrdinalIgnoreCase))
                return false;

            if (header.IndexOf('\0') >= 0)
                return false;

            if (header.IndexOf("facet", StringComparison.OrdinalIgnoreCase) >= 0)
                return true;

            if (header.IndexOf("endsolid", StringComparison.OrdinalIgnoreCase) >= 0)
                return true;

            // Fall back to a size-based heuristic: if the file length matches the binary STL layout,
            // treat it as binary even though the header starts with "solid".
            if (stream.Length >= 84)
            {
                stream.Seek(80, SeekOrigin.Begin);
                Span<byte> countBytes = stackalloc byte[4];
                if (stream.Read(countBytes) == 4)
                {
                    uint triCount = BitConverter.ToUInt32(countBytes);
                    long expectedSize = 84 + 50L * triCount;
                    stream.Position = originalPosition;
                    if (expectedSize == stream.Length)
                        return false;
                }
                stream.Position = originalPosition;
            }

            return true;
        }

        private static int CountTrianglesInStl(string filePath, bool isAscii)
        {
            if (isAscii)
            {
                int count = 0;
                using var reader = new StreamReader(filePath);
                string? line;
                while ((line = reader.ReadLine()) != null)
                {
                    if (line.TrimStart().StartsWith("facet", StringComparison.OrdinalIgnoreCase))
                        count++;
                }
                return count;
            }
            else
            {
                using var stream = File.OpenRead(filePath);
                if (stream.Length < 84)
                    return 0;

                stream.Seek(80, SeekOrigin.Begin);
                Span<byte> countBytes = stackalloc byte[4];
                if (stream.Read(countBytes) != 4)
                    return 0;

                uint triCount = BitConverter.ToUInt32(countBytes);
                return triCount > int.MaxValue ? int.MaxValue : (int)triCount;
            }
        }

        private static int GetOrAddVertex(Dictionary<VertexKey, int> lookup,
                                           List<(double X, double Y, double Z)> vertices,
                                           double x, double y, double z)
        {
            var key = CreateVertexKey(x, y, z);
            if (lookup.TryGetValue(key, out int index))
                return index;

            index = vertices.Count;
            lookup[key] = index;
            vertices.Add((x, y, z));
            return index;
        }

        private static VertexKey CreateVertexKey(double x, double y, double z)
        {
            double qx = Math.Round(x / StlMergeTolerance) * StlMergeTolerance;
            double qy = Math.Round(y / StlMergeTolerance) * StlMergeTolerance;
            double qz = Math.Round(z / StlMergeTolerance) * StlMergeTolerance;
            return new VertexKey(qx, qy, qz);
        }

        private readonly record struct VertexKey(double X, double Y, double Z);

        private static LBMGrid CreateLbmFromMetadata(DiscretizationMetaData md)
        {
            try
            {
                if (md.Generator == nameof(MeshFactory.CreateLBMGridFromPerimeter) ||
                    md.Generator == nameof(MeshFactory.CreateThoraxLBMGrid))
                {
                    md.Parameters.TryGetValue("nx", out var nxStr);
                    md.Parameters.TryGetValue("ny", out var nyStr);
                    md.Parameters.TryGetValue("electrodeCount", out var elStr);
                    md.Parameters.TryGetValue("perimeter", out var pStr);
                    int nx = int.Parse(nxStr ?? "15");
                    int ny = int.Parse(nyStr ?? "15");
                    int electrodes = int.Parse(elStr ?? "16");
                    var points = (pStr ?? string.Empty).Split(';', StringSplitOptions.RemoveEmptyEntries)
                                    .Select(s => s.Split(','))
                                    .Select(parts => (double.Parse(parts[0]), double.Parse(parts[1])))
                                    .ToList();
                    var mesh = MeshFactory.CreateLBMGridFromPerimeter(nx, ny, points, electrodes);
                    if (md.Generator == nameof(MeshFactory.CreateThoraxLBMGrid))
                        mesh.Metadata.Generator = nameof(MeshFactory.CreateThoraxLBMGrid);
                    return mesh;
                }
                if (md.Generator == nameof(MeshFactory.CreateRectangularLBMGrid))
                {
                    md.Parameters.TryGetValue("nx", out var nxStr);
                    md.Parameters.TryGetValue("ny", out var nyStr);
                    md.Parameters.TryGetValue("electrodeCount", out var elStr);
                    int nx = int.Parse(nxStr ?? "15");
                    int ny = int.Parse(nyStr ?? "15");
                    int electrodes = int.Parse(elStr ?? "16");
                    return MeshFactory.CreateRectangularLBMGrid(nx, ny, electrodes);
                }
                if (md.Generator == "LBMCreateCircular")
                {
                    md.Parameters.TryGetValue("nx", out var nxStr);
                    md.Parameters.TryGetValue("ny", out var nyStr);
                    md.Parameters.TryGetValue("radius", out var rStr);
                    md.Parameters.TryGetValue("electrodeCount", out var elStr);
                    int nx = int.Parse(nxStr ?? "15");
                    int ny = int.Parse(nyStr ?? "15");
                    int r = int.Parse(rStr ?? "10");
                    int electrodes = int.Parse(elStr ?? "16");
                    var parameters = new DiscretizationParameters { MeshType = DiscretizationType.LBM, Nx = nx, Ny = ny, Radius = r, ElectrodeCount = electrodes };
                    return (LBMGrid)MeshFactory.Create(parameters);
                }
            }
            catch
            {
            }
            return new LBMGrid();
        }
    }
}

