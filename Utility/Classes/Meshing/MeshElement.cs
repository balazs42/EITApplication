namespace Utility.Classes.Meshing
{
    public abstract class MeshElement
    {
        public int Id { get; set; } = -1;
        public List<Vertex> Vertices = [];               
    }
}
