namespace Utility.Exports
{
    public record ReconstructionInfo(string Name, string FilePath, ReconstructionMetadata Metadata)
    {
        public string ParameterSummary => string.Join(", ", Metadata.Parameters.Select(p => $"{p.Key}={p.Value}"));
    }
}
