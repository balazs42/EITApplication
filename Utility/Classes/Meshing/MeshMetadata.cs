using System;
using System.Collections.Generic;

namespace Utility.Classes.Meshing
{
    public class MeshMetadata
    {
        public DateTime CreatedOn { get; set; } = DateTime.UtcNow;
        public string Generator { get; set; } = string.Empty;
        /// <summary>
        /// Number of elements contained in the mesh when it was generated.
        /// </summary>
        public int ElementCount { get; set; } = 0;
        public Dictionary<string, string> Parameters { get; set; } = new();
    }
}
