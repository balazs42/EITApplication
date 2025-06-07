namespace Utility.Classes.Meshing
{
    public class FEMMesh : Mesh
    {
        public new List<FEMElement> Elements { get; set; } = [];

        public FEMMesh(List<Vertex> vertices, List<FEMElement> elements)
        {
            Vertices = vertices;
            Elements = elements;
        }

        public FEMMesh()
        {

        }
    }
}
