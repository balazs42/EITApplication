using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
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
            // Update metadata before serialization
            mesh.Metadata.ElementCount = mesh.GetElements().Count;
            var doc = BuildFemDocument(mesh, name);
            SaveDocument(doc, name, "fem");
        }

        public void SaveLBMMesh(LBMMesh mesh, string name)
        {
            if (mesh == null) throw new ArgumentNullException(nameof(mesh));
            if (string.IsNullOrWhiteSpace(name)) throw new ArgumentException("Name required.", nameof(name));
            // Update metadata before serialization
            mesh.Metadata.ElementCount = mesh.GetElements().Count;
            var doc = BuildLbmDocument(mesh, name);
            SaveDocument(doc, name, "lbm");
        }

        public FEMMesh LoadFEMMesh(string filePath)
        {
            if (!File.Exists(filePath))
                throw new FileNotFoundException($"Mesh file not found: {filePath}");

            var doc = XDocument.Load(filePath);
            var root = doc.Root ?? throw new InvalidOperationException("Invalid mesh file.");

            var metadata = DeserializeMetadata(root.Element("Metadata"));
            FEMMesh mesh = CreateFemFromMetadata(metadata);

            // overwrite vertex data including neighbor relationships
            var vertexMap = mesh.Vertices.ToDictionary(v => v.GlobalId);
            var neighborMap = new Dictionary<int, List<int>>();
            var verticesEl = root.Element("Vertices");
            if (verticesEl != null)
            {
                foreach (var vx in verticesEl.Elements("Vertex"))
                {
                    int id = (int)vx.Attribute("id");
                    if (vertexMap.TryGetValue(id, out var v))
                    {
                        v.X = (double)vx.Attribute("x");
                        v.Y = (double)vx.Attribute("y");
                        v.Potential = (double)vx.Attribute("potential");
                        v.IsBoundary = (bool)vx.Attribute("isBoundary");
                        v.IsElectrode = (bool)vx.Attribute("isElectrode");
                        v.BoundaryId = (int)vx.Attribute("boundaryId");
                        v.ElectrodeId = (int)vx.Attribute("electrodeId");
                        var nids = vx.Element("NeighborIds")?.Elements("NeighborId").Select(n => (int)n).ToList() ?? new List<int>();
                        neighborMap[id] = nids;
                    }
                }
                // apply neighbors after all vertices processed to avoid circular reference issues
                foreach (var kv in neighborMap)
                {
                    if (vertexMap.TryGetValue(kv.Key, out var v))
                    {
                        v.Neighbors = kv.Value.Where(vertexMap.ContainsKey).Select(id => vertexMap[id]).ToList();
                    }
                }
            }

            // elements
            var elementsEl = root.Element("Elements");
            if (elementsEl != null)
            {
                var newElements = new List<FEMElement>();
                foreach (var e in elementsEl.Elements("Element"))
                {
                    int id = (int)e.Attribute("id");
                    var ids = e.Element("VertexIds")?.Elements("VertexId").Select(vx => (int)vx).ToList() ?? new List<int>();
                    if (ids.Count == 3 && vertexMap.ContainsKey(ids[0]) && vertexMap.ContainsKey(ids[1]) && vertexMap.ContainsKey(ids[2]))
                    {
                        var el = new FEMElement(id, vertexMap[ids[0]], vertexMap[ids[1]], vertexMap[ids[2]])
                        {
                            Conductivity = (double)e.Attribute("conductivity"),
                            Permittivity = (double)e.Attribute("permittivity")
                        };
                        newElements.Add(el);
                    }
                }
                if (newElements.Count > 0)
                    mesh.SetElements(newElements);
            }

            // electrodes
            var electrodes = root.Element("Electrodes")?.Elements("Electrode").Select(e =>
            {
                var el = new FEMElectrode(
                    id: (int)e.Attribute("id"),
                    meshId: (int)e.Attribute("meshId"),
                    current: (double)e.Attribute("current"),
                    zContact: (double)e.Attribute("zContact"),
                    voltage: (double)e.Attribute("voltage"),
                    isExcitation: (bool)e.Attribute("isExcitation"),
                    isGround: (bool)e.Attribute("isGround"),
                    isMeasuring: (bool)e.Attribute("isMeasuring"),
                    pointElectrode: (bool)e.Attribute("pointElectrode")
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
                              .ToDictionary(v => (int)v.Attribute("elementId"), v => (double)v.Attribute("sigma"))
                          ?? new Dictionary<int, double>();
            mesh.SetConductivityDistribution(new ConductivityDistribution(cdDict));

            var pdDict = root.Element("PotentialDistribution")?.Elements("Value")
                              .ToDictionary(v => (int)v.Attribute("id"), v => (double)v.Attribute("phi"))
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
                var elementDict = mesh.GetElements().Cast<LBMElement>().ToDictionary(e => e.Id);
                foreach (var e in elementsEl.Elements("Element"))
                {
                    int id = (int)e.Attribute("id");
                    if (elementDict.TryGetValue(id, out var el))
                    {
                        el.Conductivity = (double)e.Attribute("conductivity");
                        el.Permittivity = (double)e.Attribute("permittivity");
                        el.IsWall = (bool)e.Attribute("isWall");
                        el.IsElectrode = (bool)e.Attribute("isElectrode");
                        var fiVals = e.Element("Fi")?.Elements("Val").Select(v => (double)v).ToArray();
                        if (fiVals != null)
                        {
                            for (int i = 0; i < Math.Min(9, fiVals.Length); i++)
                                el.Fi[i] = fiVals[i];
                        }
                    }
                }
            }

            // electrodes
            var electrodes = root.Element("Electrodes")?.Elements("Electrode").Select(e =>
                new LBMElectrode(
                    id: (int)e.Attribute("id"),
                    gridId: (int)e.Attribute("gridId"),
                    current: (double)e.Attribute("current"),
                    contactImpedance: (double)e.Attribute("zContact"),
                    potential: (double)e.Attribute("voltage"),
                    isExcitation: (bool)e.Attribute("isExcitation"),
                    isGround: (bool)e.Attribute("isGround"),
                    isMeasuring: (bool)e.Attribute("isMeasuring"))
            ).ToList() ?? new List<LBMElectrode>();
            if (electrodes.Count > 0)
                mesh.SetElectrodes(electrodes);

            var cdDict = root.Element("ConductivityDistribution")?.Elements("Value")
                              .ToDictionary(v => (int)v.Attribute("elementId"), v => (double)v.Attribute("sigma"))
                          ?? new Dictionary<int, double>();
            mesh.SetConductivityDistribution(new ConductivityDistribution(cdDict));

            var pdDict = root.Element("PotentialDistribution")?.Elements("Value")
                              .ToDictionary(v => (int)v.Attribute("id"), v => (double)v.Attribute("phi"))
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
            var doc = new XDocument(
                new XElement("FEMMesh",
                    new XAttribute("name", name),
                    SerializeMetadata(mesh.Metadata),
                    new XElement("Elements",
                        mesh.ElementsTyped.Select(el =>
                            new XElement("Element",
                                new XAttribute("id", el.Id),
                                new XAttribute("conductivity", el.Conductivity),
                                new XAttribute("permittivity", el.Permittivity),
                                new XElement("VertexIds",
                                    el.Vertices.Select(v => new XElement("VertexId", v.GlobalId)))
                            )
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
                                new XAttribute("electrodeId", v.ElectrodeId),
                                v.Neighbors.Count > 0 ?
                                    new XElement("NeighborIds", v.Neighbors.Select(n => new XElement("NeighborId", n.GlobalId))) : null
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

        private static XDocument BuildLbmDocument(LBMMesh mesh, string name)
        {
            var doc = new XDocument(
                new XElement("LBMMesh",
                    new XAttribute("name", name),
                    new XAttribute("nx", mesh.Nx),
                    new XAttribute("ny", mesh.Ny),
                    SerializeMetadata(mesh.Metadata),
                    new XElement("Elements",
                        mesh.ElementsTyped.Cast<LBMElement>().Select(el =>
                            new XElement("Element",
                                new XAttribute("id", el.Id),
                                new XAttribute("conductivity", el.Conductivity),
                                new XAttribute("permittivity", el.Permittivity),
                                new XAttribute("isWall", el.IsWall),
                                new XAttribute("isElectrode", el.IsElectrode),
                                new XElement("Fi",
                                    Enumerable.Range(0, 9).Select(i => new XElement("Val", el.Fi[i])))
                            )
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
                    dict[(string)p.Attribute("key")] = (string)p.Attribute("value");
            }
            md.Parameters = dict;
            return md;
        }

        private static FEMMesh CreateFemFromMetadata(MeshMetadata md)
        {
            try
            {
                if (md.Generator == nameof(MeshFactory.CreateCircularFEMMesh))
                {
                    md.Parameters.TryGetValue("layers", out var layersStr);
                    md.Parameters.TryGetValue("boundaryFEMVertexCount", out var boundaryStr);
                    md.Parameters.TryGetValue("electrodeCount", out var elStr);
                    md.Parameters.TryGetValue("inhomogeneityValue", out var inhStr);
                    int layers = int.Parse(layersStr ?? "1");
                    int boundary = int.Parse(boundaryStr ?? "8");
                    int electrodes = int.Parse(elStr ?? "16");
                    double inh = double.Parse(inhStr ?? "3.0");
                    return MeshFactory.CreateCircularFEMMesh(layers, boundary, electrodes, inh);
                }
                if (md.Generator == nameof(MeshFactory.CreateRectangularFEMMesh))
                {
                    md.Parameters.TryGetValue("width", out var wStr);
                    md.Parameters.TryGetValue("height", out var hStr);
                    md.Parameters.TryGetValue("electrodeCount", out var elStr);
                    double width = double.Parse(wStr ?? "1");
                    double height = double.Parse(hStr ?? "1");
                    int electrodes = int.Parse(elStr ?? "16");
                    return MeshFactory.CreateRectangularFEMMesh(width, height, electrodes);
                }
                if (md.Generator == nameof(MeshFactory.CreatePolygonFEMMesh) || md.Generator == nameof(MeshFactory.CreateThoraxFEMMesh))
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

