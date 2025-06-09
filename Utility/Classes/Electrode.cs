namespace Utility.Classes
{
    public sealed class Electrode
    {
        public int Id { get; } // Logical id associated to the electrode. Goes from 0-15.

        // Global index of this electrode (0 … Ne-1).</summary>
        public int MeshId { get; }

        public List<int> VertexIds { get; } = [];

        // The vertices that physically belong to this electrode.</summary>
        // public IReadOnlyList<int> VertexIds { get; }

        // Injected current in ampere (positive: leaving domain).</summary>
        public double Current { get; set; }

        // Contact impedance (Ω).  0 → voltage measured directly
        // Common phantom / thoracic values: ~ 0.01 Ω – 0.1 Ω.
        public double ZContact { get; set; }

        // Measured voltage value on the electrode
        public double Voltage { get; set; }

        public bool IsExcitation { get; set; } = false;
        public bool IsGround { get; set; } = false;
        public bool IsMeasuring { get; set; } = false;

        public Electrode(int meshId, List<int> vertexIds, double current = double.NaN, double zContact = 0.0, double voltage = double.NaN)
        {
            MeshId = meshId;
            Current = current;
            ZContact = zContact;
            Voltage = voltage;
            VertexIds = vertexIds;
        }

        public Electrode(int id, int meshId, double current, double zContact, double voltage, bool isExcitation = false, bool isGround = false, bool isMeasuring = false)
        {
            Id = id;
            MeshId = meshId;
            Current = current;
            ZContact = zContact;
            Voltage = voltage;
            IsExcitation = isExcitation;
            IsGround = isGround;
            IsMeasuring = isMeasuring;
        }
    }

}
