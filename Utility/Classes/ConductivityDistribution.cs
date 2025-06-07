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
    }

    /// <summary>
    /// This static class is used to generate prior distributions for a given mesh.
    /// </summary>
    public static class PriorConductivityDistributionGenerator
    {
        public static ConductivityDistribution GenerateHomogeneousDistribution(IMesh mesh)
        {
            if (mesh is not LBMMesh || mesh is not FEMMesh)
                throw new InvalidDataException("Cannot create prior distribution, if mesh type is not specified. Check code!");

            // Get current distribution
            var currentConductivity = mesh.GetConductivityDistribution();

            // Create new distribution object to return
            ConductivityDistribution newConductivityDistribution = new(currentConductivity.Conductivities);

            // All conduncitivites will be set to 1.0
            foreach (var kvp in newConductivityDistribution.Conductivities)
                newConductivityDistribution.Conductivities[kvp.Key] = 1.0;

            return newConductivityDistribution;
        }        
    }
}
