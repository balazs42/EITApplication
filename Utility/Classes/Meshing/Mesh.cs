using Utility.Classes.Meshing;

namespace Utility.Classes
{
    public abstract class Mesh
    {
        public List<Vertex> Vertices;

        public Mesh(int numVertices)
        {
            Vertices = new List<Vertex>();

            for(int i = 0; i <  numVertices; i++) 
                Vertices.Add(new Vertex(i));
        }

        public Mesh(List<Vertex> vertices)
        {
            Vertices = vertices;
        }

        public Mesh()
        {

        }
    }
}
