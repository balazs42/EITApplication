using System.Diagnostics;
using Utility.Classes.Solvers;

namespace Utility.Classes
{
    public class PotentialDistribution : ScalarField
    {
        public override Dictionary<int, double> IdValuePairs { get; set; }
        // Maps FEMVertex.GlobalId to its potential value.
        public Dictionary<int, double> Potentials { get; set; }

        public PotentialDistribution(Dictionary<int, double> potentials)
        {
            IdValuePairs = new Dictionary<int, double>();
            Potentials = potentials;
        }

        public double GetPotential(int FEMVertexId)
        {
            return Potentials.TryGetValue(FEMVertexId, out double potential) ? potential : 0.0;
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

        public override Dictionary<int, double> Get() => Potentials;
        public override void Set(Dictionary<int, double> potentials) => Potentials = potentials;
        public override double GetValue(int key) => Potentials[key];
        public override void SetValue(int key, double value) => Potentials[key] = value;
    }
}
