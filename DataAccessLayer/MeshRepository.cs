using System.Xml.Linq;
using Utility.Classes;
using Utility.Classes.Meshing;
using Utility.Classes.Meshing.FiniteElementMesh;
using Utility.Classes.Meshing.LatticeBoltzmannMesh;
using Utility.Classes.Factories;

namespace DataAccessLayer
{
    public class MeshRepository : IMeshRepository
    {
        private static string MeshDir => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                                                       "EITApplication", "Meshes");

        public void SaveFEMMesh(FEMMesh mesh, string name)
        {
            if (mesh == null) throw new ArgumentNullException(nameof(mesh));
            if (string.IsNullOrWhiteSpace(name)) throw new ArgumentException("Name required.", nameof(name));

            var doc = BuildFemDocument(mesh, name);
            SaveDocument(doc, name, "fem");
        }

        public void SaveLBMMesh(LBMMesh mesh, string name)
        {
            if (mesh == null) throw new ArgumentNullException(nameof(mesh));
            if (string.IsNullOrWhiteSpace(name)) throw new ArgumentException("Name required.", nameof(name));

            var doc = BuildLbmDocument(mesh, name);
            SaveDocument(doc, name, "lbm");
        }

        public IEnumerable<MeshInfo> GetMeshes()
        {
            Directory.CreateDirectory(MeshDir);
            foreach (var file in Directory.GetFiles(MeshDir, "*.eitmesh"))
            {
                MeshInfo? info = null;
                try
                {
                    var doc = XDocument.Load(file);
                    var root = doc.Root;
                    if (root == null) continue;
                    var name = root.Attribute("name")?.Value ?? Path.GetFileNameWithoutExtension(file);
                    var md = DeserializeMetadata(root.Element("Metadata"));
                    info = new MeshInfo(name, file, md);
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
        }

        public FEMMesh LoadFEMMesh(string filePath)
        {
            if (!File.Exists(filePath))
                throw new FileNotFoundException($"Mesh file not found: {filePath}");

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
                mesh.SetElectrodes(electrodes);

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

        public LBMMesh LoadLBMMesh(string filePath)
        {
            if (!File.Exists(filePath))
                throw new FileNotFoundException($"Mesh file not found: {filePath}");

            var doc = XDocument.Load(filePath);
            var root = doc.Root ?? throw new InvalidOperationException("Invalid mesh file.");

            var metadata = DeserializeMetadata(root.Element("Metadata"));
            LBMMesh mesh = CreateLbmFromMetadata(metadata);

            // elements
            var elementsEl = root.Element("Elements");
            if (elementsEl != null)
            {
                foreach (var el in elementsEl.Elements("Element"))
                {
                    int id = (int)(el.Attribute("id") ?? throw new NullReferenceException());
                    var e = mesh.ElementsTyped.FirstOrDefault(x => x.Id == id);
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
                mesh.SetElectrodes(electrodes);

            var cdDict = root.Element("ConductivityDistribution")?.Elements("Value")
                              .ToDictionary(v => (int)((v.Attribute("elementId") ?? throw new NullReferenceException()) ?? throw new NullReferenceException()),
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

        private static void SaveDocument(XDocument doc, string name, string suffix)
        {
            Directory.CreateDirectory(MeshDir);
            string safeName = string.Join("_", name.Split(Path.GetInvalidFileNameChars()));
            string file = Path.Combine(MeshDir, $"{safeName}_{DateTime.UtcNow:yyyyMMdd_HHmmss}_{suffix}.eitmesh");
            doc.Save(file);
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

        private static XDocument BuildLbmDocument(LBMMesh mesh, string name)
        {
            mesh.Metadata.ElementCount = mesh.ElementsTyped.Count;
            var doc = new XDocument(
                new XElement("LBMMesh",
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

        private static XElement SerializeMetadata(MeshMetadata metadata)
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

        private static MeshMetadata DeserializeMetadata(XElement? element)
        {
            var md = new MeshMetadata();
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

        private static FEMMesh CreateFemFromMetadata(MeshMetadata md)
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
                    double width = double.Parse(wStr ?? "1");
                    double height = double.Parse(hStr ?? "1");
                    int electrodes = int.Parse(elStr ?? "16");
                    return MeshFactory.CreateRectangularFEMMesh(width, height, electrodes);
                }
                string polygon = nameof(MeshFactory.CreatePolygonFEMMesh);
                if (md.Generator == polygon || md.Generator == nameof(MeshFactory.CreateThoraxFEMMesh))
                {
                    md.Parameters.TryGetValue("perimeter", out var pStr);
                    md.Parameters.TryGetValue("electrodeCount", out var elStr);
                    var points = (pStr ?? string.Empty).Split(';', StringSplitOptions.RemoveEmptyEntries)
                                    .Select(s => s.Split(','))
                                    .Select(parts => (double.Parse(parts[0]), double.Parse(parts[1])))
                                    .ToList();
                    int electrodes = int.Parse(elStr ?? "16");
                    var mesh = MeshFactory.CreatePolygonFEMMesh(points, electrodes);
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

        private static LBMMesh CreateLbmFromMetadata(MeshMetadata md)
        {
            try
            {
                if (md.Generator == nameof(MeshFactory.CreateLBMMeshFromPerimeter) ||
                    md.Generator == nameof(MeshFactory.CreateThoraxLBMMesh))
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
                    var mesh = MeshFactory.CreateLBMMeshFromPerimeter(nx, ny, points, electrodes);
                    if (md.Generator == nameof(MeshFactory.CreateThoraxLBMMesh))
                        mesh.Metadata.Generator = nameof(MeshFactory.CreateThoraxLBMMesh);
                    return mesh;
                }
                if (md.Generator == nameof(MeshFactory.CreateRectangularLBMMesh))
                {
                    md.Parameters.TryGetValue("nx", out var nxStr);
                    md.Parameters.TryGetValue("ny", out var nyStr);
                    md.Parameters.TryGetValue("electrodeCount", out var elStr);
                    int nx = int.Parse(nxStr ?? "15");
                    int ny = int.Parse(nyStr ?? "15");
                    int electrodes = int.Parse(elStr ?? "16");
                    return MeshFactory.CreateRectangularLBMMesh(nx, ny, electrodes);
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
                    var parameters = new MeshParameters { MeshType = MeshType.LBM, Nx = nx, Ny = ny, Radius = r, ElectrodeCount = electrodes };
                    return (LBMMesh)MeshFactory.Create(parameters);
                }
            }
            catch
            {
            }
            return new LBMMesh();
        }
    }
}

