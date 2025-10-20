using System;
using System.Collections.Generic;

namespace Utility.Exports
{
    public class ReconstructionMetadata
    {
        public DateTime CreatedOn { get; set; } = DateTime.UtcNow;
        public Dictionary<string, string> Parameters { get; set; } = [];
        public int FrameCount { get; set; }
    }
}
