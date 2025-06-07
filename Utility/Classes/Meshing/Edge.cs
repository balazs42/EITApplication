namespace Utility.Classes.Meshing
{
    /// <summary>
    /// Represents an edge in the FEM model.
    /// </summary>
    public class Edge
    {
        /// <summary>
        /// Start vertex of the edge.
        /// </summary>
        public Vertex Start { get; set; } = new(0, 0.0, 0.0);

        /// <summary>
        /// End vertex of the edge.
        /// </summary>
        public Vertex End { get; set; } = new(0, 0.0, 0.0);

        public Edge(Vertex start, Vertex end)
        {
            Start = start;
            End = end;
        }
    }
}
