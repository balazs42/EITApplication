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
}
