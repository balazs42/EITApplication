using System.Diagnostics;
using Utility.Classes.Meshing;

namespace Utility.Classes
{
    public sealed class ConductivityDistribution
    {
        public Dictionary<int, double> Conductivities { get; set; }

        public ConductivityDistribution(Dictionary<int, double> conductivities)
        {
            Conductivities = conductivities;
        }

        /// <summary>
        /// Safely retrieves the conductivity for a given element ID.
        /// </summary>
        /// <param name="elementId">The unique ID of the element.</param>
        /// <returns>The conductivity of the element if found; otherwise, returns 0.0.</returns>
        public double GetConductivity(int elementId)
        {
            // This is the implementation for the method you asked about.
            // It uses TryGetValue which is the safest and most efficient way
            // to get a value from a dictionary.
            return Conductivities.TryGetValue(elementId, out double conductivity) ? conductivity : 0.0;
        }

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
    }

    /// <summary>
    /// This static class is used to generate prior distributions for a given mesh.
    /// </summary>
    public static class PriorConductivityDistributionGenerator
    {
        /// <summary>
        /// Generates a homogeneous conductivity distribution where every element has the same value.
        /// </summary>
        /// <param name="mesh">The mesh to generate the distribution for.</param>
        /// <param name="defaultValue">The homogeneous conductivity value to assign to every element.</param>
        /// <returns>A new ConductivityDistribution.</returns>
        public static ConductivityDistribution GenerateHomogeneousDistribution(IMesh mesh, double defaultValue = 1.0)
        {
            if (mesh?.GetElements() == null)
                throw new ArgumentNullException(nameof(mesh), "Mesh and its elements cannot be null.");

            // Create a new dictionary from the mesh elements,
            // assigning the default value to each one.
            var conductivityDict = mesh.GetElements()
                                       .ToDictionary(element => element.Id, element => defaultValue);

            return new ConductivityDistribution(conductivityDict);
        }
    }
}
