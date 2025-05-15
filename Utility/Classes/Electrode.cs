namespace Utility.Classes
{
    public sealed class Electrode
    {
        // Global index of this electrode (0 … Ne-1).</summary>
        public int Id { get; }

        // The vertices that physically belong to this electrode.</summary>
        public IReadOnlyList<int> VertexIds { get; }

        // Injected current in ampere (positive: leaving domain).</summary>
        public double Current { get; set; }

        // Contact impedance (Ω).  0 → voltage measured directly
        // Common phantom / thoracic values: ~ 0.01 Ω – 0.1 Ω.
        public double ZContact { get; set; }

        public Electrode(int id, IEnumerable<int> vertexIds, double current = 0.0, double zContact = 0.0)
        {
            Id = id;
            VertexIds = vertexIds.ToArray();
            Current = current;
            ZContact = zContact;
        }
    }

}
