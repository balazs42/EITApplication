namespace Utility.Classes.Meshing
{
    /// <summary>
    /// Represents an edge in the FEM model.
    /// </summary>
    public class Edge
    {
        /// <summary>
        /// Start FEMVertex of the edge.
        /// </summary>
        public FEMVertex Start { get; set; } = new(0, 0.0, 0.0);

        /// <summary>
        /// End FEMVertex of the edge.
        /// </summary>
        public FEMVertex End { get; set; } = new(0, 0.0, 0.0);

        public int Id { get; set; } 

        public Edge(FEMVertex start, FEMVertex end, int id)
        {
            Start = start;
            End = end;
            Id = id;
        }
    }
}
