namespace Utility.Classes.Meshing
{
    public class LBMMesh : Mesh
    {
        public int Nx { get; }
        public int Ny { get; }

        // helper: vertexId <--> (x,y); one vertex per cell centre
        public int ToVertexId(int x, int y) => y * Nx + x;
        public (int x, int y) ToLattice(int id) => (id % Nx, id / Nx);

        public LBMMesh(int nx, int ny)
        {
            Nx = nx; 
            Ny = ny;

            Vertices = new List<Vertex>(nx * ny);

            for (int y = 0; y < ny; ++y)
                for (int x = 0; x < nx; ++x)
                    Vertices.Add(new Vertex {Id = ToVertexId(x, y), X = x, Y = y });
        }

        public LBMMesh()
        {

        }
    }
}
