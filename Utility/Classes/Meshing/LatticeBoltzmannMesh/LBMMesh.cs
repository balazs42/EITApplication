using Utility.Classes.Factories;

namespace Utility.Classes.Meshing.LatticeBoltzmannMesh
{
    public class LBMMesh : Mesh<LBMElement, LBMElectrode>
    {
        private const int _defaultNx = 15;
        private const int _defaultNy = 15;

        public int Nx { get; }
        public int Ny { get; }

        // Added for fast, direct access to elements by coordinate
        private readonly LBMElement[,] _grid;

        public LBMElement GetElementAt(int x, int y) => _grid[x, y];

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
            _grid = new LBMElement[Nx, Ny];
            for (int y = 0; y < Ny; y++)
            {
                for (int x = 0; x < Nx; x++)
                {
                    var element = new LBMElement(isWall: false) { Id = y * Nx + x };

                    if (x == 0 || x == nx - 1 || y == 0 || y == ny - 1)
                        element.IsWall = true;

                    _elements.Add(element);
                    _grid[x, y] = element;
                }
            }
            // Link neighbors for every element
            var directions = new (int cx, int cy)[] { (0, 0), (1, 0), (0, 1), (-1, 0), (0, -1), (1, 1), (-1, 1), (-1, -1), (1, -1) };
            for (int y = 0; y < Ny; y++)
            {
                for (int x = 0; x < Nx; x++)
                {
                    var currentElement = _grid[x, y];
                    for (int k = 0; k < 9; k++)
                    {
                        int neighborX = x + directions[k].cx;
                        int neighborY = y + directions[k].cy;

                        // Check if the neighbor is within the grid bounds
                        if (neighborX >= 0 && neighborX < nx && neighborY >= 0 && neighborY < ny)
                            currentElement.Neighbors[k] = _grid[neighborX, neighborY];

                        // If outside bounds, the neighbor remains null.
                    }
                }
            }
            
            Dictionary<int, double> cd = new();
            foreach (var el in _elements)
                cd.Add(el.Id, el.Conductivity);
            ConductivityDistribution = new(cd);

            ConductivityDistribution = ConductivityDistributionFactory.CreateHomogeneous(this, 1.0);
            Dictionary<int, double> pd = new();

            foreach (var el in _elements)
                pd.Add(el.Id, el.Fi.Sum());

            PotentialDistribution = new PotentialDistribution(pd);

            // Place 16 equidistant electrodes inside the walls
            //PlaceEquidistantElectrodes(electrodeNum);

            //this.ConductivityDistribution = PriorConductivityDistributionGenerator.GenerateHomogeneousDistribution(this, 1.0);
        }

        public LBMMesh(List<LBMElement> elements, int nx = _defaultNy, int ny = _defaultNy)
        {
            Nx = nx;
            Ny = ny;

            if (_elements.Count != elements.Count)
                throw new ArgumentException("Cannot assign elements, lists count mismatch! Check calling code!");

            for (int i = 0; i < elements.Count; i++)
                _elements[i] = elements[i];

           int electrodeNum = _elements.Count(x => x.IsElectrode);

            foreach (var el in _electrodes)
            {
                var electrodeElement = _elements.Find(x => x.Id == el.GridId);

                if (electrodeElement == null)
                    throw new InvalidOperationException("Cannot set electrode potential since it is not assinged a corect gridId. Check calling code!");

                el.Potential = electrodeElement.Fi.Sum();
            }

            _grid = new LBMElement[nx, ny];
            for (int x = 0; x < Nx; x++)
            {
                for (int y = 0; y < Ny; y++)
                {
                    int id = x * Nx + y;
                    var correspondingElement = _elements.Find(x => x.Id == id);

                    if (correspondingElement == null)
                        throw new InvalidOperationException("Cannot set grid, element id mismatch. The ids should be at top left, and descend to bottom right. Check calling code!");
                    _grid[x, y] = correspondingElement;
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
            foreach (var el in _elements)
                el.IsElectrode = false;

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

                    var cell = _grid[ix, iy];
                    // first non‐wall is our electrode
                    if (!cell.IsWall)
                    {
                        chosenId = cell.Id;
                        break;
                    }
                }

                // fallback if ray never hit a non‐wall cell
                if (chosenId < 0)
                    chosenId = _elements.First(el => !el.IsWall).Id;

                // 4) Mark the chosen element as an electrode
                var chosenEl = _elements.Single(el => el.Id == chosenId);
                chosenEl.IsElectrode = true;

                // 5) Create and register the high‐level LBMElectrode
                var electrode = new LBMElectrode(
                    id: i,
                    gridId: chosenId,
                    current: 0.0,
                    contactImpedance: 0.0,
                    potential: 0.0
                );
                _electrodes.Add(electrode);
            }
        }

        protected override IEnumerable<int> StateKeys() => _elements.Select(v => v.Id);

        protected override void ApplyPotentialToState(int cellId, double potential)
        {
            var cell = _elements.FirstOrDefault(e => e.Id == cellId)
                       ?? throw new InvalidOperationException($"No LBMElement.Id = {cellId}.");
            // Egyszerű példa: egyenletesen osztjuk szét, hogy sum(Fi) = potential
            double eq = potential / 9.0;
            for (int i = 0; i < 9; i++) cell.Fi[i] = eq;
        }

        protected override double ReadPotentialOf(LBMElectrode e)
        {
            var cell = _elements.FirstOrDefault(c => c.Id == e.GridId)
                       ?? throw new InvalidOperationException($"No LBMElement.Id = {e.GridId} (LBMElectrode.GridId).");
            return cell.Fi.Sum();
        }

        // --- Egyéb kötelezők ---
        public override void LogMesh()
        {
            Console.WriteLine($"LBM | {Nx}x{Ny}, E={_elements.Count}, EL={_electrodes.Count}");
        }

        public override Mesh DeepCopy()
        {
            var copy = new LBMMesh(Nx, Ny);

            // copy element state
            for (int i = 0; i < _elements.Count; i++)
            {
                copy.ElementsTyped[i].Conductivity = _elements[i].Conductivity;
                var src = _elements[i];
                var dst = copy.ElementsTyped[i];

                dst.Conductivity = src.Conductivity;
                dst.IsWall = src.IsWall;
                dst.IsElectrode = src.IsElectrode;

                for (int k = 0; k < 9; k++) 
                    dst.Fi[k] = src.Fi[k];
            }

            // clone electrodes list
            var electrodes = _electrodes
                .Select(e => new LBMElectrode(
                    id: e.Id,
                    gridId: e.GridId,
                    current: e.Current,
                    potential: e.Potential,
                    contactImpedance: e.ZContact,
                    isExcitation: e.IsExcitation,
                    isGround: e.IsGround,
                    isMeasuring: e.IsMeasuring)).ToList();

            copy.SetElectrodes(electrodes);

            // clone distributions
            var cd = copy.GetElements()
                          .Cast<LBMElement>()
                          .ToDictionary(el => el.Id, el => el.Conductivity);
            copy.SetConductivityDistribution(new ConductivityDistribution(cd));

            var pd = copy.GetElements()
                          .Cast<LBMElement>()
                          .ToDictionary(el => el.Id, el => el.Fi.Sum());
            copy.SetPotentialDistribution(new PotentialDistribution(pd));

            copy.SetElements(copy.ElementsTyped.ToList());

            return copy;
        }
        public override GraphMesh.Graph ToGraph()
        {
            throw new NotImplementedException();
        }

        public override Mesh FromGraph()
        {
            throw new NotImplementedException();
        }
    }
}