namespace Utility.Classes.Meshing
{
    public class MeshMetadata
    {
        public DateTime CreatedOn { get; set; } = DateTime.UtcNow;
        public string Generator { get; set; } = string.Empty;
        public Dictionary<string, string> Parameters { get; set; } = new();
    }
}
