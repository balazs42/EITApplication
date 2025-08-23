using System;
using System.IO;
using System.Text.Json;
using Utility.Classes.Meshing;
using Utility.Classes.Meshing.FiniteElementMesh;
using Utility.Classes.Meshing.GraphMesh;
using Utility.Classes.Meshing.LatticeBoltzmannMesh;

namespace DataAccessLayer
{
    public class MeshRepository : IMeshRepository
    {
        public void SaveMesh(IMesh mesh, string name)
        {
            if (mesh == null) throw new ArgumentNullException(nameof(mesh));
            if (string.IsNullOrWhiteSpace(name)) throw new ArgumentException("Name required.", nameof(name));

            Graph graph = mesh switch
            {
                FEMMesh fem => fem.ToGraph(),
                LBMMesh lbm => lbm.ToGraph(),
                _ => throw new NotSupportedException($"Mesh type {mesh.GetType().Name} not supported.")
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
                Graph = graph
            };

            string dir = Path.Combine(AppContext.BaseDirectory, "Meshes");
            Directory.CreateDirectory(dir);
            string safeName = string.Join("_", name.Split(Path.GetInvalidFileNameChars()));
            string file = Path.Combine(dir, $"{safeName}_{model.SavedAt:yyyyMMdd_HHmmss}.json");
            var opts = new JsonSerializerOptions { WriteIndented = true };
            File.WriteAllText(file, JsonSerializer.Serialize(model, opts));
        }

        public IMesh LoadMesh(string name, DateTime savedAt)
        {
            string dir = Path.Combine(AppContext.BaseDirectory, "Meshes");
            string safeName = string.Join("_", name.Split(Path.GetInvalidFileNameChars()));
            string file = Path.Combine(dir, $"{safeName}_{savedAt:yyyyMMdd_HHmmss}.json");
            if (!File.Exists(file))
                throw new FileNotFoundException($"Mesh file not found: {file}");

            var opts = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
            var model = JsonSerializer.Deserialize<StoredMesh>(File.ReadAllText(file), opts)
                        ?? throw new InvalidOperationException("Failed to deserialize mesh.");

            return model.MeshType switch
            {
                MeshType.FEM => new FEMMesh().FromGraph(model.Graph),
                MeshType.LBM => new LBMMesh().FromGraph(model.Graph),
                _ => throw new NotSupportedException($"Unsupported mesh type {model.MeshType}.")
            };
        }

        private sealed class StoredMesh
        {
            public string Name { get; set; } = string.Empty;
            public DateTime SavedAt { get; set; }
            public MeshType MeshType { get; set; }
            public Graph Graph { get; set; } = null!;
        }

        private enum MeshType
        {
            FEM,
            LBM
        }
    }
}
