namespace Utility.Classes.Meshing
{
    public class LBMMesh : Mesh
    {
        public int Nx { get; }
        public int Ny { get; }

        // Added for fast, direct access to elements by coordinate
        private readonly LBMElement[,] _elementGrid;

        public LBMElement GetElementAt(int x, int y) => _elementGrid[x, y];

        public (int x, int y) ToLattice(int id) => (id % Nx, id / Nx);

        /// <summary>
        /// Initializes the mesh structure, by first creating the LBMElements, and initializing them
        /// and after that adds walls to the boundary, and electrodes. Finally initializes the 
        /// conductivtiy distribution defined on the mesh to be homogeneous.
        /// </summary>
        /// <param name="nx">Number of cells in the x dimension.</param>
        /// <param name="ny">Number of cells in the y dimension.</param>
        public LBMMesh(int nx = 50, int ny = 50)
        {
            Nx = nx;
            Ny = ny;

            // Create all elements and place them in a grid for easy lookup
            _elementGrid = new LBMElement[nx, ny];
            base.Elements = new List<MeshElement>(nx * ny);
            for (int y = 0; y < ny; y++)
            {
                for (int x = 0; x < nx; x++)
                {
                    var element = new LBMElement(new List<Vertex>(), isWall: false) { Id = y * nx + x };
                    if (x == 0 || x == nx - 1 || y == 0 || y == ny - 1)
                        element.IsWall = true;

                    _elementGrid[x, y] = element;
                    Elements.Add(element);
                }
            }

            // Link neighbors for every element
            var directions = new (int cx, int cy)[] { (0, 0), (1, 0), (0, 1), (-1, 0), (0, -1), (1, 1), (-1, 1), (-1, -1), (1, -1) };
            for (int y = 0; y < ny; y++)
            {
                for (int x = 0; x < nx; x++)
                {
                    var currentElement = _elementGrid[x, y];
                    for (int k = 0; k < 9; k++)
                    {
                        int neighborX = x + directions[k].cx;
                        int neighborY = y + directions[k].cy;

                        // Check if the neighbor is within the grid bounds
                        if (neighborX >= 0 && neighborX < nx && neighborY >= 0 && neighborY < ny)
                            currentElement.Neighbors[k] = _elementGrid[neighborX, neighborY];

                        // If outside bounds, the neighbor remains null.
                    }
                }
            }

            // Place 16 equidistant electrodes inside the walls
            PlaceEquidistantElectrodes(16);

            this.ConductivityDistribution = PriorConductivityDistributionGenerator.GenerateHomogeneousDistribution(this, 1.0);
        }

        /// <summary>
        /// Automatically places a specified number of electrodes equidistantly along the inner perimeter of the mesh.
        /// </summary>
        private void PlaceEquidistantElectrodes(int numElectrodes)
        {
            var perimeterCells = new List<LBMElement>();

            // Trace the inner perimeter path: top, right, bottom, left
            for (int x = 1; x < Nx - 1; x++) perimeterCells.Add(_elementGrid[x, 1]);
            for (int y = 1; y < Ny - 1; y++) perimeterCells.Add(_elementGrid[Nx - 2, y]);
            for (int x = Nx - 2; x > 0; x--) perimeterCells.Add(_elementGrid[x, Ny - 2]);
            for (int y = Ny - 2; y > 0; y--) perimeterCells.Add(_elementGrid[1, y]);

            if (perimeterCells.Count == 0) return; // Cannot place electrodes on a very small mesh

            // Initialize the main electrode list
            base.Electrodes = new List<Electrode>(numElectrodes);

            double spacing = (double)perimeterCells.Count / numElectrodes;

            for (int i = 0; i < numElectrodes; i++)
            {
                // Calculate the ideal index on the perimeter path
                int index = (int)Math.Round(i * spacing);
                // Ensure index is within bounds
                index = Math.Min(index, perimeterCells.Count - 1);

                var electrodeElement = perimeterCells[index];

                // Mark the element as an electrode
                electrodeElement.IsElectrode = true;

                // Create the corresponding high-level Electrode object.
                // For LBM, one element corresponds to one measurement point.
                var electrode = new Electrode(
                    id: i, // The electrode's logical ID (0-15)
                    meshId: electrodeElement.Id, // The element's ID within the mesh
                    current: 0.0,
                    zContact: 0.0,
                    voltage: 0.0
                );

                base.Electrodes.Add(electrode);
            }
        }

        public override double[] GetElectrodePotentials()
        {
            double[] electrodePotentials = new double[16];

            for(int i = 0; i < 16; i++)
            {
                if (Electrodes[i].IsMeasuring)
                    electrodePotentials[i] = Electrodes[i].Voltage;
                else
                    electrodePotentials[i] = double.NaN;
            }

            return electrodePotentials;
        }
    }
}