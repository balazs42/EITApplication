using System.Text.Json;
using System.Text.Json.Serialization;
using Utility.Classes;
using Utility.Classes.Meshing;
using Utility.Classes.Meshing.FiniteElementMesh;
using Utility.Classes.Meshing.LatticeBoltzmannMesh;

namespace DataAccessLayer
{
    public class MeshRepository : IMeshRepository
    {
        private static JsonSerializerOptions Options => new()
        {
            IncludeFields = true,
            ReferenceHandler = ReferenceHandler.Preserve,
            MaxDepth = 1024
        };

        private static string MeshDir => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                                                       "EITApplication", "Meshes");

        public void SaveFEMMesh(FEMMesh mesh, string name) => Save(mesh, name, "fem");
        public void SaveLBMMesh(LBMMesh mesh, string name) => Save(mesh, name, "lbm");

        public FEMMesh LoadFEMMesh(string filePath) => Load<FEMMesh>(filePath, fem =>
        {
            var cd = fem.ConductivityDistribution;
            var pd = fem.PotentialDistribution;
            fem.Initialize();
            fem.SetConductivityDistribution(cd);
            fem.SetPotentialDistribution(pd);
        });

        public LBMMesh LoadLBMMesh(string filePath) => Load<LBMMesh>(filePath, lbm =>
        {
            lbm.RebuildGrid();
            lbm.SetConductivityDistribution(lbm.ConductivityDistribution);
            lbm.SetPotentialDistribution(lbm.PotentialDistribution);
        });

        private void Save<TMesh>(TMesh mesh, string name, string suffix) where TMesh : Mesh
        {
            if (mesh == null) throw new ArgumentNullException(nameof(mesh));
            if (string.IsNullOrWhiteSpace(name)) throw new ArgumentException("Name required.", nameof(name));

            var model = new StoredMesh<TMesh>
            {
                Name = name,
                SavedAt = DateTime.UtcNow,
                Metadata = mesh.Metadata,
                Mesh = mesh
            };

            Directory.CreateDirectory(MeshDir);
            string safeName = string.Join("_", name.Split(Path.GetInvalidFileNameChars()));
            string file = Path.Combine(MeshDir, $"{safeName}_{model.SavedAt:yyyyMMdd_HHmmss}_{suffix}.json");
            var opts = new JsonSerializerOptions { WriteIndented = true, MaxDepth = 1024 };
            File.WriteAllText(file, JsonSerializer.Serialize(model, opts));
        }

        private TMesh Load<TMesh>(string filePath, Action<TMesh> fixer) where TMesh : Mesh
        {
            if (!File.Exists(filePath))
                throw new FileNotFoundException($"Mesh file not found: {filePath}");

            var opts = new JsonSerializerOptions { PropertyNameCaseInsensitive = true, MaxDepth = 1024 };
            var model = JsonSerializer.Deserialize<StoredMesh<TMesh>>(File.ReadAllText(filePath), opts)
                        ?? throw new InvalidOperationException("Failed to deserialize mesh.");

            TMesh mesh = model.Mesh ?? throw new InvalidOperationException("Mesh payload missing.");
            mesh.Metadata = model.Metadata ?? new MeshMetadata();
            fixer(mesh);
            return mesh;
        }

        private sealed class StoredMesh<T>
        {
            public string Name { get; set; } = string.Empty;
            public DateTime SavedAt { get; set; }
            public MeshMetadata? Metadata { get; set; }
            public T? Mesh { get; set; }
        }
    }
}
