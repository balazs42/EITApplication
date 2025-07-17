using Utility.Classes.Factories;

namespace Utility.Classes.Meshing.LatticeBoltzmannMesh
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
        public LBMMesh(int nx = _defaultNx, int ny = _defaultNy, int electrodeNum = 16)
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
            PlaceEquidistantElectrodes(electrodeNum);

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
        /// Place N electrodes roughly equidistant in angle around the domain
        /// by ray‐casting from the outer radius toward the center until a non‐wall
        /// cell is found along each ray.
        /// </summary>
        public void PlaceEquidistantElectrodes(int numElectrodes)
        {
            // 1) Clear any existing electrode flags
            foreach (var el in Elements)
                el.IsElectrode = false;

            Electrodes.Clear();
            base.Electrodes.Clear();

            if (numElectrodes <= 0) return;

            // 2) Compute center and max radius in lattice coords
            double cx = (Nx - 1) / 2.0;
            double cy = (Ny - 1) / 2.0;
            double maxR = Math.Min(cx, cy);

            // 3) For each electrode, pick an angle and ray‐cast inward
            for (int i = 0; i < numElectrodes; i++)
            {
                double theta = 2.0 * Math.PI * i / numElectrodes;
                int chosenId = -1;

                // Step from outer radius toward center in small increments
                for (double r = maxR; r >= 0; r -= 0.5)
                {
                    // continuous coords along ray
                    double fx = cx + r * Math.Cos(theta);
                    double fy = cy + r * Math.Sin(theta);
                    int ix = (int)Math.Round(fx);
                    int iy = (int)Math.Round(fy);

                    // skip out‐of‐bounds
                    if (ix < 0 || ix >= Nx || iy < 0 || iy >= Ny)
                        continue;

                    var cell = _elementGrid[ix, iy];
                    // first non‐wall is our electrode
                    if (!cell.IsWall)
                    {
                        chosenId = cell.Id;
                        break;
                    }
                }

                // fallback if ray never hit a non‐wall cell
                if (chosenId < 0)
                    chosenId = Elements.First(el => !el.IsWall).Id;

                // 4) Mark the chosen element as an electrode
                var chosenEl = Elements.Single(el => el.Id == chosenId);
                chosenEl.IsElectrode = true;

                // 5) Create and register the high‐level LBMElectrode
                var electrode = new LBMElectrode(
                    id: i,
                    gridId: chosenId,
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
            var copy = new LBMMesh(Nx, Ny);

            // 2) copy element‐by‐element
            foreach (var orig in Elements)
            {
                // locate the corresponding new element by id:
                var (x, y) = ToLattice(orig.Id);
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
            foreach (var OE in Electrodes)
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
                new Dictionary<int, double>(ConductivityDistribution.Conductivities)
            );
            copy.PotentialDistribution = new PotentialDistribution(
                new Dictionary<int, double>(PotentialDistribution.Potentials)
            );

            return copy;
        }


        public new void SetPotentialDistribution(PotentialDistribution potentialDistribution)
        {
            PotentialDistribution = potentialDistribution;
            base.PotentialDistribution = potentialDistribution;

            foreach(var kvp in PotentialDistribution.Potentials)
            {
                var correspondingElectrode = Electrodes.Find(x => x.Id == kvp.Key);

                if(correspondingElectrode != null ) 
                    correspondingElectrode.Potential = kvp.Value;
            }
        }

        public void SetConductivity(int id, double value)
        {
            var element = Elements.Find(x=>x.Id == id);

            if (element == null)
                throw new NullReferenceException("Cannot set conductivity of element, can not find Id!");

            element.Conductivity = value;
        }
    }
}