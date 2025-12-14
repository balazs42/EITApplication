namespace Utility.Classes.Discretizer.FiniteElementMesh
{
    /// <summary>
    /// Represents an edge in the FEM model.
    /// </summary>
    public class Edge
    {
        /// <summary>
        /// Start FEM vertex of the edge.
        /// </summary>
        public FEMVertex Start { get; set; } = new(0, 0.0, 0.0);

        /// <summary>
        /// End FEM vertex of the edge.
        /// </summary>
        public FEMVertex End { get; set; } = new(0, 0.0, 0.0);

        /// <summary>
        /// Unique edge identifier.
        /// </summary>
        public int Id { get; set; }

        /// <summary>
        /// True when the edge belongs to the exterior boundary of the mesh.
        /// </summary>
        public bool IsBoundary { get; set; }

        /// <summary>
        /// Length of the edge in the current coordinate system.
        /// </summary>
        public double Length
        {
            get
            {
                double dx = Start.X - End.X;
                double dy = Start.Y - End.Y;
                return Math.Sqrt(dx * dx + dy * dy);
            }
        }

        public Edge(FEMVertex start, FEMVertex end, int id)
        {
            Start = start;
            End = end;
            Id = id;
        }
    }
}
