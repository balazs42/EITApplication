using System.Text.Json;
using System.Text.Json.Serialization;
using Utility.Classes.Meshing.FiniteElementMesh;
using Utility.Classes.Meshing.LatticeBoltzmannMesh;
using Utility.Classes;

namespace DataAccessLayer
{
    public class MeshRepository : IMeshRepository
    {
        private static IMesh? _mesh = null;

        public void SaveMesh(IMesh mesh, string name)
        {
            if (mesh == null) throw new ArgumentNullException(nameof(mesh));
            if (string.IsNullOrWhiteSpace(name)) throw new ArgumentException("Name required.", nameof(name));

            var meshOptions = new JsonSerializerOptions
            {
                IncludeFields = true,
                ReferenceHandler = ReferenceHandler.Preserve
            };

            var model = new StoredMesh
            {
                Name = name,
                SavedAt = DateTime.UtcNow,
                MeshType = mesh switch
                {
                    FEMMesh => MeshType.FEM,
                    LBMMesh => MeshType.LBM,
                    _ => throw new NotSupportedException($"Mesh type {mesh.GetType().Name} not supported.")
                },
                Mesh = JsonSerializer.SerializeToElement(mesh, mesh.GetType(), meshOptions)
            };

            string dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                                       "EITApplication", "Meshes");
            Directory.CreateDirectory(dir);
            string safeName = string.Join("_", name.Split(Path.GetInvalidFileNameChars()));
            string file = Path.Combine(dir, $"{safeName}_{model.SavedAt:yyyyMMdd_HHmmss}.json");
            var opts = new JsonSerializerOptions { WriteIndented = true };
            File.WriteAllText(file, JsonSerializer.Serialize(model, opts));
        }

        public IMesh LoadMesh(string filePath)
        {
            if (!File.Exists(filePath))
                throw new FileNotFoundException($"Mesh file not found: {filePath}");

            var opts = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
            var model = JsonSerializer.Deserialize<StoredMesh>(File.ReadAllText(filePath), opts)
                        ?? throw new InvalidOperationException("Failed to deserialize mesh.");

            var meshOpts = new JsonSerializerOptions
            {
                IncludeFields = true,
                ReferenceHandler = ReferenceHandler.Preserve
            };

            IMesh mesh = model.MeshType switch
            {
                MeshType.FEM =>
                    model.Mesh.Deserialize<FEMMesh>(meshOpts)
                        ?? throw new InvalidOperationException("Failed to deserialize FEM mesh."),
                MeshType.LBM =>
                    model.Mesh.Deserialize<LBMMesh>(meshOpts)
                        ?? throw new InvalidOperationException("Failed to deserialize LBM mesh."),
                _ => throw new NotSupportedException($"Unsupported mesh type {model.MeshType}.")
            };

            switch (mesh)
            {
                case FEMMesh fem:
                    var cd = fem.ConductivityDistribution;
                    var pd = fem.PotentialDistribution;
                    fem.Initialize();
                    fem.SetConductivityDistribution(cd);
                    fem.SetPotentialDistribution(pd);
                    return fem;
                case LBMMesh lbm:
                    lbm.RebuildGrid();
                    lbm.SetConductivityDistribution(lbm.ConductivityDistribution);
                    lbm.SetPotentialDistribution(lbm.PotentialDistribution);
                    return lbm;
                default:
                    throw new NotSupportedException($"Unsupported mesh instance {mesh.GetType().Name}.");
            }
        }

        private sealed class StoredMesh
        {
            public string Name { get; set; } = string.Empty;
            public DateTime SavedAt { get; set; }
            public MeshType MeshType { get; set; }
            public JsonElement Mesh { get; set; }
        }

        private enum MeshType
        {
            FEM,
            LBM
        }
    }
}
