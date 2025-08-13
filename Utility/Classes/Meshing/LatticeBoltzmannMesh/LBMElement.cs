namespace Utility.Classes.Meshing.LatticeBoltzmannMesh
{

    /// <summary>
    /// (-1, 1)     (0, 1)     (1, 1)
    /// (-1, 0)     (0, 0)     (1, 0)
    /// (-1,-1)     (0,-1)     (1,-1)
    /// </summary>
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
        
        public LBMElement() { }
        public LBMElement(bool isWall)
        {
            IsWall = isWall;
        }

        public LBMElement(int id, LBMElement[] neighbors, double[] fi, double[] fi_next, double conductivity, bool isWall, bool isElectrode)
        {
            Id = id;
            Neighbors = neighbors;
            Fi = fi;
            Fi_next = fi_next;
            Conductivity = conductivity;
            IsWall = isWall;
            IsElectrode = isElectrode;
        }

        public double GetPotential()
        {
            double sum = 0.0;
            for (int i = 0; i < 9; i++)
                sum += Fi[i];

            return sum;
        }

        public double GetXCurrent()
        {
            //return Fi[2] - Fi[4] + Math.Sqrt(2) / 2.0 * (Fi[5] + Fi[8] - Fi[6] - Fi[7]);
            // East-West contributions + diagonals (NE, SE, NW, SW)
            return Fi[1] - Fi[3] + Math.Sqrt(2) / 2.0 * (Fi[5] + Fi[8] - Fi[6] - Fi[7]);
        }

        public double GetYCurrent()
        {
            //return Fi[1] - Fi[3] + Math.Sqrt(2) / 2.0 * (Fi[5] + Fi[6] - Fi[7] - Fi[8]);
            // North-South contributions + diagonals
            return Fi[2] - Fi[4] + Math.Sqrt(2) / 2.0 * (Fi[5] + Fi[6] - Fi[7] - Fi[8]);
        }

        public double GetCurrentAmplitude()
        {
            double vx = GetXCurrent();
            double vy = GetYCurrent();
            return Math.Sqrt(vx * vx + vy * vy);
        }
    }
}
