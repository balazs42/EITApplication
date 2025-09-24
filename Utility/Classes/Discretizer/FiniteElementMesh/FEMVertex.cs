namespace Utility.Classes.Discretizer.FiniteElementMesh
{
    public class FEMVertex
    {
        // X,Y is the location of the FEMVertex in the mesh
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

        public List<FEMVertex> Neighbors { get; set; } = [];

        public FEMVertex(int globalId)
        {
            GlobalId = globalId;
            X = 0.0;
            Y = 0.0;
        }

        public FEMVertex()
        {

        }

        public FEMVertex(double x, double y, int globalId, List<FEMVertex> neighbors, int boundaryId = -1, int electrodeId = -1)
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

        public FEMVertex(int globalId, double x, double y)
        {
            GlobalId = globalId;
            X = x;
            Y = y;
        }

        public FEMVertex(int globalId, int boundaryId, int electrodeId, double x, double y, bool isBoundary, bool isElectrode)
        {
            GlobalId = globalId;
            BoundaryId = boundaryId;
            ElectrodeId = electrodeId;
            X = x;
            Y = y;
            IsBoundary = isBoundary;
            IsElectrode = isElectrode;
        }

        public FEMVertex(int globalId, int boundaryId, int electrodeId, double x, double y, bool isBoundary, bool isElectrode, double potential)
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
