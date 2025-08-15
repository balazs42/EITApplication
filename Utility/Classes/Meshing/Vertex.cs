namespace Utility.Classes.Meshing
{
    public class Vertex
    {
        // X,Y is the location of the vertex in the mesh
        public double X { get; set; } = 0.0;
        public double Y { get; set; } = 0.0;

        // Ids serve as the indexers
        public int GlobalId { get; set; } = -1;
        public int BoundaryId { get; set; } = -1;
        public int ElectrodeId { get; set; } = -1;
        public double Potential { get; set; } = 0.0;

        // Booelans help case checking
        public bool IsBoundary { get; set; } = false;
        public bool IsElectrode { get; set; } = false;

        public List<Vertex> Neighbors { get; set; } = [];

        public Vertex(int globalId)
        {
            GlobalId = globalId;
            X = 0.0;
            Y = 0.0;
        }

        public Vertex()
        {

        }

        public Vertex(double x, double y, int globalId, List<Vertex> neighbors, int boundaryId = -1, int electrodeId = -1)
        {
            X = x;
            Y = y;
            GlobalId = globalId;
            Neighbors = neighbors;

            if(boundaryId > -1)
            {
                BoundaryId = boundaryId;
                IsBoundary = true;
            }
            else if(electrodeId > -1)
            {
                ElectrodeId = electrodeId;
                IsElectrode = true;
            }
        }

        public Vertex(int globalId, double x, double y)
        {
            GlobalId = globalId;
            X = x;
            Y = y;
        }

        public Vertex(int globalId, int boundaryId, int electrodeId, double x, double y, bool isBoundary, bool isElectrode)
        {
            GlobalId = globalId;
            BoundaryId = boundaryId;
            ElectrodeId = electrodeId;
            X = x;
            Y = y;
            IsBoundary = isBoundary;
            IsElectrode = isElectrode;
        }

        public Vertex(int globalId, int boundaryId, int electrodeId, double x, double y, bool isBoundary, bool isElectrode, double potential)
        {
            GlobalId = globalId;
            BoundaryId = boundaryId;
            ElectrodeId = electrodeId;
            X = x;
            Y = y;
            IsBoundary = isBoundary;
            IsElectrode = isElectrode;
            Potential = potential;  
        }
    }
}
