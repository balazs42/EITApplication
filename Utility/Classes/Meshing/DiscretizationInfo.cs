using System.Linq;

namespace Utility.Classes.Meshing
{
    public record DiscretizationInfo(string Name, string FilePath, DiscretizationMetaData Metadata)
    {
        public string ParameterSummary => string.Join(", ", Metadata.Parameters.Select(p => $"{p.Key}={p.Value}"));
        public string Generator => Metadata.Generator.ToString();
    }
}
