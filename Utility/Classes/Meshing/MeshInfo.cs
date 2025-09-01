using System.Linq;

namespace Utility.Classes.Meshing
{
    public record MeshInfo(string Name, string FilePath, MeshMetadata Metadata)
    {
        public string ParameterSummary => string.Join(", ", Metadata.Parameters.Select(p => $"{p.Key}={p.Value}"));
    }
}
