namespace Utility.Classes.Meshing
{
    public class LBMMesh : Mesh
    {
        public int Nx { get; }
        public int Ny { get; }

        // helper: vertexId <--> (x,y); one vertex per cell centre
        public int ToVertexId(int x, int y) => y * Nx + x;
        public (int x, int y) ToLattice(int id) => (id % Nx, id / Nx);

        public LBMMesh(int nx = 50, int ny = 50)
        {
            Nx = nx; 
            Ny = ny;

            Vertices = new List<Vertex>(nx * ny);

            for (int y = 0; y < ny; ++y)
                for (int x = 0; x < nx; ++x)
                    Vertices.Add(new Vertex {GlobalId = ToVertexId(x, y), X = x, Y = y });
        }
    }
}
