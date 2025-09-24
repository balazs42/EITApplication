namespace Utility.Classes.Discretizer
{
    public record DiscretizationInfo(string Name, string FilePath, DiscretizationMetaData Metadata)
    {
        public string ParameterSummary => string.Join(", ", Metadata.Parameters.Select(p => $"{p.Key}={p.Value}"));
        public string Generator => Metadata.Generator.ToString();
    }
}
