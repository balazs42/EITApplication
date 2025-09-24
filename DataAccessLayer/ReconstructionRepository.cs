using System.Xml.Linq;
using Utility.Classes;
using Utility.Classes.ReconstructionParameters;

namespace DataAccessLayer
{
    public class ReconstructionRepository : IReconstructionRepository
    {
        private static string ReconDir => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                                                        "EITApplication", "Reconstructions");

        public void SaveReconstruction(List<ReconstructionResult> frames, string name, EITReconstructionParameters parameters)
        {
            if (frames == null || frames.Count == 0) throw new ArgumentException("No frames to save.", nameof(frames));
            if (string.IsNullOrWhiteSpace(name)) throw new ArgumentException("Name required.", nameof(name));

            var md = new ReconstructionMetadata
            {
                CreatedOn = DateTime.UtcNow,
                FrameCount = frames.Count,
                Parameters = new Dictionary<string, string>
                {
                    ["DifferentialEquationSolver"] = parameters.DifferentialEquationSolver.ToString(),
                    ["RegularizationTechnique"] = parameters.RegularizationTechnique.ToString(),
                    ["ErrorMetric"] = parameters.ErrorMetric.ToString(),
                    ["NumericSolver"] = parameters.NumericSolver.ToString(),
                    ["NumericOptimizer"] = parameters.NumericOptimizer.ToString(),
                    ["Mesh"] = parameters.Mesh.ToString()
                }
            };

            var doc = new XDocument(
                new XElement("Reconstruction",
                    new XAttribute("name", name),
                    SerializeMetadata(md),
                    new XElement("Frames",
                        frames.Select((f, i) =>
                            new XElement("Frame",
                                new XAttribute("index", i),
                                SerializeConductivity("Original", f.OriginalConductivityDistribution),
                                SerializeConductivity("Initial", f.InitialConductivitiyDistribution),
                                SerializeConductivity("Reconstructed", f.ReconstructedConductivityDistribution)
                            ))
                    )
                )
            );

            Directory.CreateDirectory(ReconDir);
            doc.Save(Path.Combine(ReconDir, $"{name}.eitrecon"));
        }

        public IEnumerable<ReconstructionInfo> GetReconstructions()
        {
            Directory.CreateDirectory(ReconDir);
            foreach (var file in Directory.GetFiles(ReconDir, "*.eitrecon"))
            {
                ReconstructionInfo? info = null;
                try
                {
                    var doc = XDocument.Load(file);
                    var root = doc.Root;
                    if (root == null) continue;
                    var name = root.Attribute("name")?.Value ?? Path.GetFileNameWithoutExtension(file);
                    var md = DeserializeMetadata(root.Element("Metadata"));
                    info = new ReconstructionInfo(name, file, md);
                }
                catch
                {
                    // ignore invalid files
                }

                if (info != null)
                    yield return info;
            }
        }

        public List<ReconstructionResult> LoadReconstruction(string filePath)
        {
            if (!File.Exists(filePath))
                throw new FileNotFoundException($"Reconstruction file not found: {filePath}");

            var doc = XDocument.Load(filePath);
            var root = doc.Root ?? throw new InvalidOperationException("Invalid reconstruction file.");

            var frames = new List<ReconstructionResult>();
            var framesEl = root.Element("Frames");
            if (framesEl != null)
            {
                foreach (var frameEl in framesEl.Elements("Frame").OrderBy(f => (int)(f.Attribute("index") ?? throw new NullReferenceException())))
                {
                    var orig = DeserializeConductivity(frameEl.Element("Original"));
                    var init = DeserializeConductivity(frameEl.Element("Initial"));
                    var recon = DeserializeConductivity(frameEl.Element("Reconstructed"));
                    frames.Add(new ReconstructionResult(orig,
                                                        init,
                                                        recon,
                                                        new List<ReconstructionFrame>()));
                }
            }
            return frames;
        }

        private static XElement SerializeMetadata(ReconstructionMetadata metadata)
        {
            return new XElement("Metadata",
                new XElement("CreatedOn", metadata.CreatedOn.ToString("o")),
                new XElement("FrameCount", metadata.FrameCount),
                new XElement("Parameters",
                    metadata.Parameters.Select(p =>
                        new XElement("Parameter",
                            new XAttribute("key", p.Key),
                            new XAttribute("value", p.Value)))
                )
            );
        }

        private static ReconstructionMetadata DeserializeMetadata(XElement? element)
        {
            var md = new ReconstructionMetadata();
            if (element == null) return md;

            md.CreatedOn = DateTime.Parse(element.Element("CreatedOn")?.Value ?? DateTime.UtcNow.ToString("o"));
            md.FrameCount = int.Parse(element.Element("FrameCount")?.Value ?? "0");
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

        private static XElement SerializeConductivity(string name, ConductivityDistribution dist)
        {
            return new XElement(name,
                dist.Conductivities.Select(kv =>
                    new XElement("Value",
                        new XAttribute("elementId", kv.Key),
                        new XAttribute("sigma", kv.Value))));
        }

        private static ConductivityDistribution DeserializeConductivity(XElement? element)
        {
            var dict = element?.Elements("Value").ToDictionary(v => (int)(v.Attribute("elementId") ?? throw new NullReferenceException()),
                                                              v => (double)(v.Attribute("sigma") ?? throw new NullReferenceException()))
                       ?? new Dictionary<int, double>();
            return new ConductivityDistribution(dict);
        }
    }
}