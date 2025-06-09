using System.Diagnostics;

namespace Utility.Classes
{
    public class PotentialDistribution
    {
        // Maps Vertex.GlobalId to its potential value.
        public Dictionary<int, double> Potentials { get; }

        public PotentialDistribution(Dictionary<int, double> potentials)
        {
            Potentials = potentials;
        }

        public double GetPotential(int vertexId)
        {
            return Potentials.TryGetValue(vertexId, out double potential) ? potential : 0.0;
        }

        public void LogDistribution(int nx = 15, int ny = 15)
        {
            for(int i = 0; i < nx; i++)
            {
                for (int j = 0; j < ny - 1; j++)
                    Debug.Write($"{Potentials[i * nx + j].ToString("0.0000", System.Globalization.CultureInfo.InvariantCulture)},");
                Debug.Write($"{Potentials[i*nx + ny-1].ToString("0.0000", System.Globalization.CultureInfo.InvariantCulture)};\n");
            }
        }
    }
}
