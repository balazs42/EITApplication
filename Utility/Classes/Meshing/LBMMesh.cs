namespace Utility.Classes.Meshing
{
    public class LBMMesh : Mesh
    {
        public int Nx { get; }
        public int Ny { get; }

        // Added for fast, direct access to elements by coordinate
        private readonly LBMElement[,] _elementGrid;

        public LBMElement GetElementAt(int x, int y) => _elementGrid[x, y];

        // helper: vertexId <--> (x,y); one vertex per cell centre
        public int ToVertexId(int x, int y) => y * Nx + x;
        public (int x, int y) ToLattice(int id) => (id % Nx, id / Nx);

        public LBMMesh(int nx = 50, int ny = 50)
        {
            Nx = nx;
            Ny = ny;

            // A grid of (nx x ny) cells requires (nx+1 x ny+1) vertices.
            int numVertices = (nx + 1) * (ny + 1);
            Vertices = new List<Vertex>(numVertices);

            for (int y = 0; y <= ny; y++)
                for (int x = 0; x <= nx; x++)
                    Vertices.Add(new Vertex { GlobalId = y * (nx + 1) + x, X = x, Y = y });
            
            // Create the grid of LBM elements
            _elementGrid = new LBMElement[nx, ny];
            Elements = new List<MeshElement>(nx * ny); // From base class
            for (int y = 0; y < ny; y++)
            {
                for (int x = 0; x < nx; x++)
                {
                    // Get the 4 corner vertices for this cell
                    Vertex v1 = Vertices[y * (nx + 1) + x];       // Bottom-left
                    Vertex v2 = Vertices[y * (nx + 1) + (x + 1)];   // Bottom-right
                    Vertex v3 = Vertices[(y + 1) * (nx + 1) + x];   // Top-left
                    Vertex v4 = Vertices[(y + 1) * (nx + 1) + (x + 1)]; // Top-right

                    var element = new LBMElement(new List<Vertex> { v1, v2, v3, v4 }, isWall: false)
                    {
                        Id = y * nx + x
                    };
                    _elementGrid[x, y] = element;
                    Elements.Add(element);
                }
            }
        }
    }
}
