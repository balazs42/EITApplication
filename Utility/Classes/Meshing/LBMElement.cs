namespace Utility.Classes.Meshing
{
    public sealed class LBMElement : MeshElement
    {
        /// <summary>
        /// Holds direct references to the 9 neighboring elements in D2Q9 directions.
        /// An entry will be null if the neighbor is outside the simulation domain.
        /// Index corresponds to the D2Q9 velocity vectors (e.g., Neighbors[1] is to the right).
        /// </summary>
        public LBMElement[] Neighbors { get; } = new LBMElement[9];

        /// <summary>
        /// The 9 discrete distribution functions for this lattice cell.
        /// </summary>
        public double[] Fi { get; } = new double[9];

        /// <summary>
        /// A temporary buffer used during the streaming step to avoid data races.
        /// </summary>
        public double[] Fi_next { get; } = new double[9];

        public bool IsWall { get; set; }
        public bool IsElectrode { get; set; }
        public double Conductivity { get; set; } = 1.0;

        public LBMElement(List<Vertex> vertices, bool isWall)
        {
            base.Vertices = vertices;
            IsWall = isWall;
        }
    }
}
