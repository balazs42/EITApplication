using Utility.Classes.Factories;

namespace Utility.Classes.Meshing
{
    public class LBMMesh : Mesh
    {
        private const int _defaultNx = 15;
        private const int _defaultNy = 15;

        public int Nx { get; }
        public int Ny { get; }

        // Added for fast, direct access to elements by coordinate
        private readonly LBMElement[,] _elementGrid;

        public new List<LBMElement> Elements = [];
        public new List<LBMElectrode> Electrodes = [];

        public LBMElement GetElementAt(int x, int y) => _elementGrid[x, y];

        public (int x, int y) ToLattice(int id) => (id % Nx, id / Nx);

        /// <summary>
        /// Initializes the mesh structure, by first creating the LBMElements, and initializing them
        /// and after that adds walls to the boundary, and electrodes. Finally initializes the 
        /// conductivtiy distribution defined on the mesh to be homogeneous.
        /// </summary>
        /// <param name="nx">Number of cells in the x dimension.</param>
        /// <param name="ny">Number of cells in the y dimension.</param>
        public LBMMesh(int nx = _defaultNx, int ny = _defaultNy)
        {
            Nx = nx;
            Ny = ny;

            // Create all elements and place them in a grid for easy lookup
            _elementGrid = new LBMElement[Nx, Ny];
            Elements = new List<LBMElement>(Nx * Ny);
            for (int y = 0; y < Ny; y++)
            {
                for (int x = 0; x < Nx; x++)
                {
                    var element = new LBMElement(isWall: false) { Id = y * Nx + x };

                    if (x == 0 || x == nx - 1 || y == 0 || y == ny - 1)
                        element.IsWall = true;

                    _elementGrid[x, y] = element;
                    Elements.Add(element);
                    base.Elements.Add(element);
                }
            }
            // Link neighbors for every element
            var directions = new (int cx, int cy)[] { (0, 0), (1, 0), (0, 1), (-1, 0), (0, -1), (1, 1), (-1, 1), (-1, -1), (1, -1) };
            for (int y = 0; y < Ny; y++)
            {
                for (int x = 0; x < Nx; x++)
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
            
            Dictionary<int, double> cd = new();
            foreach (var el in Elements)
                cd.Add(el.Id, el.Conductivity);
            ConductivityDistribution = new(cd);

            ConductivityDistribution = ConductivityDistributionFactory.CreateHomogeneous(this, 1.0);
            Dictionary<int, double> pd = new();

            foreach (var el in Elements)
                pd.Add(el.Id, el.Fi.Sum());

            PotentialDistribution = new PotentialDistribution(pd);

            // Place 16 equidistant electrodes inside the walls
            PlaceEquidistantElectrodes(16);

            //this.ConductivityDistribution = PriorConductivityDistributionGenerator.GenerateHomogeneousDistribution(this, 1.0);
        }

        public LBMMesh(List<LBMElement> elements, int nx = _defaultNy, int ny = _defaultNy)
        {
            Nx = nx;
            Ny = ny;

            Elements = elements;

            int electrodeNum = Elements.Count(x => x.IsElectrode);

            Electrodes = new List<LBMElectrode>(electrodeNum);

            foreach (var el in Electrodes)
            {
                var electrodeElement = Elements.Find(x => x.Id == el.GridId);

                if (electrodeElement == null)
                    throw new InvalidOperationException("Cannot set electrode potential since it is not assinged a corect gridId. Check calling code!");

                el.Potential = electrodeElement.Fi.Sum();
            }

            _elementGrid = new LBMElement[nx, ny];
            for (int x = 0; x < Nx; x++)
            {
                for (int y = 0; y < Ny; y++)
                {
                    int id = x * Nx + y;
                    var correspondingElement = Elements.Find(x => x.Id == id);
                    if (correspondingElement == null)
                        throw new InvalidOperationException("Cannot set grid, element id mismatch. The ids should be at top left, and descend to bottom right. Check calling code!");
                    _elementGrid[x, y] = correspondingElement;
                }
            }
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
            Electrodes = new List<LBMElectrode>(numElectrodes);

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
                electrodeElement.IsWall = false;

                // Create the corresponding high-level Electrode object.
                // For LBM, one element corresponds to one measurement point.
                var electrode = new LBMElectrode(
                    id: i, // The electrode's logical ID (0-15)
                    gridId: electrodeElement.Id, // The element's ID within the mesh
                    current: 0.0,
                    contactImpedance: 0.0,
                    potential: 0.0
                );

                Electrodes.Add(electrode);
                base.Electrodes.Add(electrode);
            }
        }

        public void SetElectrodes(List<LBMElectrode> electrodes)
        {
            Electrodes = electrodes;
        }

        public override double[] GetElectrodePotentials()
        {
            double[] electrodePotentials = new double[16];

            for(int i = 0; i < 16; i++)
            {
                if (Electrodes[i].IsMeasuring)
                    electrodePotentials[i] = Electrodes[i].Potential;
                else
                    electrodePotentials[i] = double.NaN;
            }

            return electrodePotentials;
        }

        public override LBMMesh DeepCopy()
        {
            // 1) create an empty mesh of the same dimensions:
            var copy = new LBMMesh(this.Nx, this.Ny);

            // 2) copy element‐by‐element
            foreach (var orig in this.Elements)
            {
                // locate the corresponding new element by id:
                var (x, y) = this.ToLattice(orig.Id);
                var dst = copy.GetElementAt(x, y);

                // copy flags + conductivity:
                dst.IsWall = orig.IsWall;
                dst.IsElectrode = orig.IsElectrode;
                dst.Conductivity = orig.Conductivity;

                // deep‐copy the two 9‐velocity arrays:
                for (int k = 0; k < 9; k++)
                {
                    dst.Fi[k] = orig.Fi[k];
                    dst.Fi_next[k] = orig.Fi_next[k];
                }

                // neighbors were already wired by the ctor
            }

            // 3) copy the high‐level electrode objects
            copy.Electrodes.Clear();
            base.Electrodes.Clear();
            foreach (var OE in this.Electrodes)
            {
                var NE = new LBMElectrode(
                    id: OE.Id,
                    gridId: OE.GridId,
                    current: OE.Current,
                    contactImpedance: OE.ZContact,
                    potential: OE.Potential
                )
                {
                    IsMeasuring = OE.IsMeasuring
                    // copy any other electrode flags here…
                };

                copy.Electrodes.Add(NE);
                base.Electrodes.Add(NE);
            }

            // 4) clone your distributions
            copy.ConductivityDistribution = new ConductivityDistribution(
                new Dictionary<int, double>(this.ConductivityDistribution.Conductivities)
            );
            copy.PotentialDistribution = new PotentialDistribution(
                new Dictionary<int, double>(this.PotentialDistribution.Potentials)
            );

            return copy;
        }

    }
}