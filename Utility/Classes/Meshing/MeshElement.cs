namespace Utility.Classes.Meshing
{
    public abstract class MeshElement
    {
        public int Id { get; set; } = -1;
        public List<Vertex> Vertices = [];
        public bool IsPinned { get; set; } = false;
        public double PinValue { get; set; } = 0.0;
    }
}
