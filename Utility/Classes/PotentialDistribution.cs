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
    }
}
