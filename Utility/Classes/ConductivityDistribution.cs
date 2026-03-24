using System.Diagnostics;
using Utility.Classes.Solvers;

namespace Utility.Classes
{
    public sealed class ConductivityDistribution : ScalarField
    {
        public override Dictionary<int, double> IdValuePairs
        {
            get => Conductivities;
            set => Conductivities = value ?? [];
        }

        public Dictionary<int, double> Conductivities { get; set; }

        public ConductivityDistribution(Dictionary<int, double> conductivities)
        {
            Conductivities = conductivities ?? [];
        }

        /// <summary>
        /// Safely retrieves the conductivity for a given element ID.
        /// </summary>
        /// <param name="elementId">The unique ID of the element.</param>
        /// <returns>The conductivity of the element if found; otherwise, returns 0.0.</returns>
        public double GetConductivity(int elementId)
            => Conductivities.TryGetValue(elementId, out double conductivity) ? conductivity : 0.0;

        /// <summary>
        /// Helper to convert this conductivity distribution into a format that
        /// can be used by operators expecting a potential distribution.
        /// </summary>
        public PotentialDistribution ToPotentialDistribution()
        {
            return new PotentialDistribution(this.Conductivities);
        }

        public void LogDistribution(int nx = 15, int ny = 15)
        {
            for (int i = 0; i < nx; i++)
            {
                for (int j = 0; j < ny - 1; j++)
                    Debug.Write($"{Conductivities[i * nx + j].ToString("0.0000", System.Globalization.CultureInfo.InvariantCulture)},");
                Debug.Write($"{Conductivities[i * nx + ny - 1].ToString("0.0000", System.Globalization.CultureInfo.InvariantCulture)};\n");
            }
        }

        public override Dictionary<int, double> Get() => Conductivities;
        public override void Set(Dictionary<int, double> conductivites) => Conductivities = conductivites ?? [];
        public override double GetValue(int key) => GetConductivity(key);
        public override void SetValue(int key, double value) => Conductivities[key] = value;

        public ConductivityDistribution CreateCompactHistoryClone()
            => new(new Dictionary<int, double>(Conductivities));
    }
}
