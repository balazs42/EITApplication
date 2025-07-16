namespace Utility.Classes.Meshing
{
    public class FEMElectrode : Electrode
    {

        // Global index of this electrode (0 … Ne-1).</summary>
        public int MeshId { get; }

        public List<int> VertexIds { get; } = [];

        // FEM case: Surface area of the electrode, which correspond to the integral
        // specified in %, so 0.1 means it takes length as on a connection like: Vn ----- Ve ----- Vm
        // |Ve-Vn| * 0.1 and |Ve - Vm| * 0.1
        public double Length { get; set; } = 0.3;


        public FEMElectrode(int meshId, List<int> vertexIds, double current = double.NaN, double zContact = 0.1, double voltage = double.NaN)
        {
            MeshId = meshId;
            Current = current;
            ZContact = zContact;
            Potential = voltage;
            VertexIds = vertexIds;
        }

        public FEMElectrode(int id, int meshId, double current, double zContact, double voltage, bool isExcitation = false, bool isGround = false, bool isMeasuring = false)
        {
            Id = id;
            MeshId = meshId;
            Current = current;
            ZContact = zContact;
            Potential = voltage;
            IsExcitation = isExcitation;
            IsGround = isGround;
            IsMeasuring = isMeasuring;
        }
    }
}
