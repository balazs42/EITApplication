using Utility.Classes.Factories;
using Utility.Classes.Meshing.Graph.Graph;

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

        /// <summary>
        /// Uniform grid upsampling by an integer factor (e.g., 2 = each coarse cell becomes 2x2 fine cells).
        /// Conductivity is replicated; potentials are copied.
        /// Electrodes are re-centered inside the corresponding refined block.
        /// </summary>
        public override LBMMesh RefineUniform(int factor = 2)
        {
            if (factor <= 1) return (LBMMesh)this.DeepCopy();

            int NX = Nx * factor;
            int NY = Ny * factor;
            var fine = new LBMMesh(NX, NY);

            // Map conductivity/potential
            for (int y = 0; y < Ny; y++)
                for (int x = 0; x < Nx; x++)
                {
                    var src = _grid[x, y];
                    for (int fy = y * factor; fy < (y + 1) * factor; fy++)
                        for (int fx = x * factor; fx < (x + 1) * factor; fx++)
                        {
                            var dst = fine._grid[fx, fy];
                            dst.IsWall = src.IsWall && (fx == 0 || fy == 0 || fx == NX - 1 || fy == NY - 1);
                            dst.Conductivity = src.Conductivity;

                            double pot = src.Fi.Sum();
                            double eq = pot / 9.0;
                            for (int k = 0; k < 9; k++) dst.Fi[k] = eq;
                        }
                }

            // Recreate electrodes roughly at the center of each refined block that had one
            var newElectrodes = new List<LBMElectrode>();
            foreach (var el in _electrodes)
            {
                var (cx, cy) = ToLattice(el.GridId);
                int nx = cx * factor + factor / 2;
                int ny = cy * factor + factor / 2;
                nx = Math.Clamp(nx, 1, NX - 2);
                ny = Math.Clamp(ny, 1, NY - 2);

                int newId = ny * NX + nx;
                newElectrodes.Add(new LBMElectrode(
                    id: el.Id,
                    gridId: newId,
                    current: el.Current,
                    potential: el.Potential,
                    contactImpedance: el.ZContact,
                    isExcitation: el.IsExcitation,
                    isGround: el.IsGround,
                    isMeasuring: el.IsMeasuring));
            }
            fine.SetElectrodes(newElectrodes);

            // Refresh distributions for fine
            var cd = fine.GetElements().ToDictionary(e => e.Id, e => ((LBMElement)e).Conductivity);
            fine.SetConductivityDistribution(new ConductivityDistribution(cd));

            var pd = fine.GetElements().ToDictionary(e => e.Id, e => ((LBMElement)e).Fi.Sum());
            fine.SetPotentialDistribution(new PotentialDistribution(pd));

            return fine;
        }

        public override GraphMesh.Graph ToGraph()
        {
            // Build a graph from NON-WALL cells only.
            // Nodes: interior lattice cells (x,y) with !IsWall
            // Edges: 4-neighbor (E,N,W,S) connections between interior cells
            // Weight: two-point flux τ_ij = |Γ| / (d_i/σ_i + d_j/σ_j)
            // On a unit grid: |Γ|=1, d_i=d_j=0.5  => τ_ij = 2 / (1/σ_i + 1/σ_j) (harmonic mean).

            var verts = new List<GraphVertex>();
            var idToVtx = new Dictionary<int, GraphVertex>();

            for (int y = 0; y < Ny; y++)
                for (int x = 0; x < Nx; x++)
                {
                    var cell = _grid[x, y];
                    if (cell.IsWall) continue; // exclude walls from the domain graph

                    int boundaryId = 0;
                    // mark interior boundary if any 4-neighbor is a wall or out of bounds
                    bool touchesWall =
                        x == 0 || x == Nx - 1 || y == 0 || y == Ny - 1 ||
                        _grid[Math.Max(0, x - 1), y].IsWall ||
                        _grid[Math.Min(Nx - 1, x + 1), y].IsWall ||
                        _grid[x, Math.Max(0, y - 1)].IsWall ||
                        _grid[x, Math.Min(Ny - 1, y + 1)].IsWall;

                    if (touchesWall) boundaryId = 1;

                    var gv = new GraphVertex(x, y, cell.Id, domainId: 0, boundaryId: boundaryId)
                    {
                        Potential = cell.Fi.Sum()
                    };
                    verts.Add(gv);
                    idToVtx[cell.Id] = gv;
                }

            var edges = new List<GraphEdge>();

            // Only add each edge once (east and north)
            for (int y = 0; y < Ny; y++)
                for (int x = 0; x < Nx; x++)
                {
                    var c = _grid[x, y];
                    if (c.IsWall || !idToVtx.ContainsKey(c.Id)) continue;

                    void AddEdgeTo(int nx, int ny)
                    {
                        if (nx < 0 || nx >= Nx || ny < 0 || ny >= Ny) return;
                        var n = _grid[nx, ny];
                        if (n.IsWall) return;
                        if (!idToVtx.ContainsKey(n.Id)) return;

                        double sig_i = Math.Max(c.Conductivity, 1e-15);
                        double sig_j = Math.Max(n.Conductivity, 1e-15);
                        double tau = 2.0 / (1.0 / sig_i + 1.0 / sig_j); // harmonic mean on unit grid

                        edges.Add(new GraphEdge(idToVtx[c.Id], idToVtx[n.Id], tau));
                    }

                    // East and North to avoid duplicates
                    if (x + 1 < Nx) AddEdgeTo(x + 1, y);
                    if (y + 1 < Ny) AddEdgeTo(x, y + 1);
                }

            return new GraphMesh.Graph(verts, edges);
        }

        public override LBMMesh FromGraph(GraphMesh.Graph graphToConvert)
        {
            if (graphToConvert == null) throw new ArgumentNullException(nameof(graphToConvert));
            if (graphToConvert.Vertices.Count == 0)
                throw new InvalidOperationException("Graph has no vertices.");

            // Infer a rectangular interior grid from graph vertex (x,y) coords.
            // We assume the graph came from ToGraph(): integer coords for interior cells only.
            int minX = (int)Math.Round(graphToConvert.Vertices.Min(v => v.X));
            int maxX = (int)Math.Round(graphToConvert.Vertices.Max(v => v.X));
            int minY = (int)Math.Round(graphToConvert.Vertices.Min(v => v.Y));
            int maxY = (int)Math.Round(graphToConvert.Vertices.Max(v => v.Y));

            // Add a 1-cell wall border around interior domain
            int NX = (maxX - minX + 1) + 2;
            int NY = (maxY - minY + 1) + 2;

            var mesh = new LBMMesh(NX, NY);

            // Map graph vertex -> new grid cell index (shift by +1,+1 due to wall border)
            var lookup = graphToConvert.Vertices.ToDictionary(
                v => v.GlobalId,
                v =>
                {
                    int ix = (int)Math.Round(v.X) - minX + 1;
                    int iy = (int)Math.Round(v.Y) - minY + 1;
                    return (ix, iy);
                });

            // Build adjacency for averaging edge weights per node
            var adj = new Dictionary<int, List<double>>();
            foreach (var e in graphToConvert.Edges)
            {
                int i = e.Vertices[0].GlobalId;
                int j = e.Vertices[1].GlobalId;
                if (!adj.ContainsKey(i)) adj[i] = new List<double>();
                if (!adj.ContainsKey(j)) adj[j] = new List<double>();
                adj[i].Add(e.Weight);
                adj[j].Add(e.Weight);
            }

            // Assign conductivity and potentials to corresponding interior cells
            foreach (var v in graphToConvert.Vertices)
            {
                var (ix, iy) = lookup[v.GlobalId];
                var cell = mesh._grid[ix, iy];
                cell.IsWall = false;

                // Average incident edge weights as a proxy for local conductivity
                double sigma =
                    (adj.TryGetValue(v.GlobalId, out var lst) && lst.Count > 0)
                    ? Math.Max(lst.Average(), 1e-6)
                    : 1.0;

                cell.Conductivity = sigma;

                // Set potential (distribute equally among Fi)
                double eq = v.Potential / 9.0;
                for (int k = 0; k < 9; k++) cell.Fi[k] = eq;
            }

            // Refresh distributions
            var cd = mesh.GetElements().ToDictionary(el => el.Id, el => ((LBMElement)el).Conductivity);
            mesh.SetConductivityDistribution(new ConductivityDistribution(cd));

            var pd = mesh.GetElements().ToDictionary(el => el.Id, el =>
            {
                var c = (LBMElement)el;
                return c.Fi.Sum();
            });
            mesh.SetPotentialDistribution(new PotentialDistribution(pd));

            return mesh;
        }
    }
}