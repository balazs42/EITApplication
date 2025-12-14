using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Serialization;
using Utility.Classes.Application;
using Utility.Classes.Discretizer.GraphMesh;
using Utility.Classes.Factories;
using Utility.Classes.Reconstruction.VirtualElectrodes;
using Utility.Classes.Solvers.LatticeBoltzmannSolver;

namespace Utility.Classes.Discretizer.LatticeBoltzmannGrid
{
    public class LBMGrid : Discretization<LBMElement, LBMElectrode>
    {
        private const int _defaultNx = 15;
        private const int _defaultNy = 15;

        /// <summary>
        /// Toggle diagonal boundary links.  Setting this to <c>false</c> disables √2 links entirely
        /// which is useful when debugging regressions or comparing to purely axis-aligned schemes.
        /// </summary>
        internal static bool UseDiagonalBoundaryLinks = true;

        private static readonly (int cx, int cy)[] NeighborDirections =
        {
            (0, 0),
            (1, 0),
            (0, 1),
            (-1, 0),
            (0, -1),
            (1, 1),
            (-1, 1),
            (-1, -1),
            (1, -1)
        };

        public int Nx { get; }
        public int Ny { get; }

        private LBMBoundaryTopology _boundaryTopology = LBMBoundaryTopology.Empty;
        internal LBMBoundaryTopology BoundaryTopology => _boundaryTopology;

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
        private bool[,] _interiorMask;

        public LBMGrid(int nx = _defaultNx, int ny = _defaultNy)
        {
            Nx = nx;
            Ny = ny;

            _interiorMask = new bool[Nx, Ny];

            // Create all elements and place them in a grid for easy lookup
            _grid = new LBMElement[Nx, Ny];
            for (int y = 0; y < Ny; y++)
            {
                for (int x = 0; x < Nx; x++)
                {
                    var element = new LBMElement(isWall: false) { Id = y * Nx + x };

                    _elements.Add(element);
                    _grid[x, y] = element;
                }
            }

            // Link neighbors for every element
            for (int y = 0; y < Ny; y++)
            {
                for (int x = 0; x < Nx; x++)
                {
                    var currentElement = _grid[x, y];
                    for (int k = 0; k < 9; k++)
                    {
                        int neighborX = x + NeighborDirections[k].cx;
                        int neighborY = y + NeighborDirections[k].cy;

                        // Check if the neighbor is within the grid bounds
                        if (neighborX >= 0 && neighborX < nx && neighborY >= 0 && neighborY < ny)
                            currentElement.Neighbors[k] = _grid[neighborX, neighborY];

                        // If outside bounds, the neighbor remains null.
                    }
                }
            }
            
            Dictionary<int, double> cd = [];
            foreach (var el in _elements)
                cd.Add(el.Id, el.Conductivity);
            ConductivityDistribution = new(cd);

            ConductivityDistribution = ConductivityDistributionFactory.CreateHomogeneous(this, 1.0);
            Dictionary<int, double> pd = [];

            foreach (var el in _elements)
                pd.Add(el.Id, el.Fi.Sum());

            PotentialDistribution = new PotentialDistribution(pd);

            // Default domain: the physical region occupies the interior of the grid
            // and the outermost ring becomes the one-cell-thick ghost layer.
            ApplyRectangularDomain(1, Nx - 2, 1, Ny - 2);
        }

        /// <summary>
        /// Defines an axis-aligned rectangular physical domain inside the lattice.  Cells whose
        /// centres fall inside the box become part of the conductive domain, while the cells just
        /// outside form the one-cell-thick ghost layer.  Coordinates are inclusive and expressed in
        /// lattice indices.
        /// </summary>
        public void ApplyRectangularDomain(int xmin, int xmax, int ymin, int ymax)
        {
            var mask = new bool[Nx, Ny];
            for (int y = 0; y < Ny; y++)
            {
                for (int x = 0; x < Nx; x++)
                {
                    mask[x, y] = x >= xmin && x <= xmax && y >= ymin && y <= ymax;
                }
            }

            ApplyDomainMask(mask);
        }

        /// <summary>
        /// Defines a circular physical domain.  All lattice cells whose centres satisfy
        /// (x-cx)^2 + (y-cy)^2 ≤ radius^2 are treated as interior.  Cells that lie within one
        /// lattice spacing outside the circle become ghost cells.
        /// </summary>
        public void ApplyCircularDomain(double cx, double cy, double radius)
        {
            var mask = new bool[Nx, Ny];
            double r2 = radius * radius;

            for (int y = 0; y < Ny; y++)
            {
                double dy = y + 0.5 - cy;
                for (int x = 0; x < Nx; x++)
                {
                    double dx = x + 0.5 - cx;
                    mask[x, y] = dx * dx + dy * dy <= r2;
                }
            }

            ApplyDomainMask(mask);
        }

        /// <summary>
        /// Applies a custom interior mask.  True entries mark conductive (interior) cells, while
        /// false entries are outside the physical domain.  A one-cell-thick ghost layer is created
        /// automatically outside the interior region.
        /// </summary>
        public void ApplyDomainMask(bool[,] interiorMask)
        {
            if (interiorMask.GetLength(0) != Nx || interiorMask.GetLength(1) != Ny)
                throw new ArgumentException("Interior mask dimensions must match the grid size.");

            _interiorMask = new bool[Nx, Ny];
            Array.Copy(interiorMask, _interiorMask, interiorMask.Length);

            RebuildGhostLayerFromMask();
        }

        /// <summary>
        /// Mirrors ghost conductivities from their current interior neighbours.  Reconstruction updates never
        /// touch ghosts directly, therefore we recompute them before every solve to avoid stale interface jumps.
        /// </summary>
        public void UpdateGhostConductivityFromNeighbors()
        {
            if (ConductivityDistribution is null)
                return;

            foreach (var cell in _elements)
            {
                if (!cell.GhostElement)
                    continue;

                double mirrored = double.NaN;

                for (int dir = 1; dir < NeighborDirections.Length; dir++)
                {
                    var neighbor = cell.Neighbors[dir];
                    if (neighbor is null || neighbor.IsWall || neighbor.GhostElement)
                        continue;

                    double sigmaNeighbor = ConductivityDistribution.GetConductivity(neighbor.Id);
                    if (!double.IsFinite(sigmaNeighbor) || sigmaNeighbor <= 0.0)
                        continue;

                    mirrored = sigmaNeighbor;
                    break; // Mirror the live interior value touching this ghost cell.
                }

                if (!double.IsFinite(mirrored) || mirrored <= 0.0)
                    mirrored = 1.0; // Fallback to unity if no valid neighbour was found.

                cell.Conductivity = mirrored;
                ConductivityDistribution.Conductivities[cell.Id] = mirrored;
            }
        }

        private void RebuildGhostLayerFromMask()
        {
            for (int y = 0; y < Ny; y++)
            {
                for (int x = 0; x < Nx; x++)
                {
                    bool interior = _interiorMask[x, y];
                    var cell = _grid[x, y];

                    if (interior)
                    {
                        cell.IsWall = false;
                        cell.GhostElement = false;
                        continue;
                    }

                    bool touchesInterior = false;
                    double sigmaSum = 0.0;
                    int sigmaCount = 0;

                    for (int dir = 1; dir < NeighborDirections.Length; dir++)
                    {
                        var (dx, dy) = NeighborDirections[dir];
                        int nx = x + dx;
                        int ny = y + dy;

                        if (nx < 0 || nx >= Nx || ny < 0 || ny >= Ny)
                            continue;

                        if (_interiorMask[nx, ny])
                        {
                            touchesInterior = true;
                            sigmaSum += _grid[nx, ny].Conductivity;
                            sigmaCount++;
                        }
                    }

                    cell.IsWall = true;
                    cell.IsElectrode = false;
                    cell.ElectrodeId = -1;
                    cell.GhostElement = touchesInterior;

                    if (touchesInterior)
                    {
                        double sigma = sigmaCount > 0 ? sigmaSum / sigmaCount : 1.0;
                        if (!double.IsFinite(sigma) || sigma <= 0.0)
                            sigma = 1.0;
                        cell.Conductivity = sigma;
                        ConductivityDistribution.Conductivities[cell.Id] = sigma;
                    }
                }
            }

            RebuildBoundaryTopologyFromState();
            UpdateGhostConductivityFromNeighbors();
        }

        public LBMGrid(List<LBMElement> elements, int nx = _defaultNy, int ny = _defaultNy)
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

            _interiorMask = new bool[Nx, Ny];
            for (int y = 0; y < Ny; y++)
                for (int x = 0; x < Nx; x++)
                    _interiorMask[x, y] = !_grid[x, y].IsWall;

            RebuildGhostLayerFromMask();
        }

        /// <summary>
        /// Place N electrodes roughly equidistant in angle around the domain
        /// by ray‐casting from the outer radius toward the center until a non‐wall
        /// cell is found along each ray.
        /// </summary>
        public void PlaceEquidistantElectrodes(int numElectrodes, VirtualElectrodeSettings? virtualElectrodeSettings = null)
        {
            foreach (var el in _elements)
            {
                el.IsElectrode = false;
                el.ElectrodeId = -1;
            }

            if (numElectrodes <= 0)
            {
                _electrodes.Clear();
                UpdateGhostLayer();
                return;
            }

            UpdateGhostLayer();

            var boundaryRing = GetBoundaryRing();
            if (boundaryRing.Count == 0)
            {
                Workspace.AddWarningMessage("Cannot place electrodes: no boundary cells found.");
                return;
            }

            // ← ADD THIS: Filter to only valid interior-adjacent boundary cells
            var validBoundary = boundaryRing
                .Where(b => b.InteriorCell != null && !b.InteriorCell.IsWall && !b.InteriorCell.GhostElement)
                .ToList();
            
            if (validBoundary.Count < numElectrodes)
            {
                Workspace.AddWarningMessage($"Only {validBoundary.Count} valid boundary cells found, cannot place {numElectrodes} electrodes.");
                return;
            }

            double angleStep = 2.0 * Math.PI / numElectrodes;
            var electrodes = new List<LBMElectrode>(numElectrodes);
            
            // ← CHANGED: Use validBoundary instead of boundaryRing
            double[] boundaryAngles = validBoundary.Select(b => b.Angle).ToArray();
            bool[] used = new bool[validBoundary.Count];

            for (int i = 0; i < numElectrodes; i++)
            {
                double targetAngle = i * angleStep;
                int boundaryIndex = FindClosestAvailableBoundaryIndex(targetAngle, boundaryAngles, used);
                
                if (boundaryIndex < 0)
                {
                    Workspace.AddWarningMessage($"Cannot place electrode {i}: no available boundary cell.");
                    continue;
                }

                used[boundaryIndex] = true;

                // ← CHANGED: Use the valid interior cell instead of the wall cell
                var interiorCell = validBoundary[boundaryIndex].InteriorCell!;
                
                var electrode = new LBMElectrode(
                    id: i,
                    gridId: interiorCell.Id,  // ← Use interior cell ID, not wall cell
                    current: 0.0,
                    potential: 0.0,
                    contactImpedance: 1.0,
                    isVirtual: false);
                
                electrodes.Add(electrode);
                
                // Mark the interior cell as an electrode contact
                interiorCell.IsElectrode = true;
            }

            SetElectrodes(electrodes);
            
            if (virtualElectrodeSettings != null)
                ApplyVirtualElectrodes(virtualElectrodeSettings);
        }

        public new void SetElectrodes(IList<LBMElectrode> electrodes)
        {
            base.SetElectrodes(electrodes);

            foreach (var cell in _elements.Cast<LBMElement>())
            {
                cell.IsElectrode = false;
                cell.ElectrodeId = -1;
            }

            foreach (var electrode in _electrodes)
            {
                var (ex, ey) = ToLattice(electrode.GridId);
                if (ex < 0 || ex >= Nx || ey < 0 || ey >= Ny)
                    continue;

                var cell = _grid[ex, ey];
                cell.IsElectrode = true;
                cell.ElectrodeId = electrode.Id;
                cell.IsWall = false;
                cell.GhostElement = false;
                if (_interiorMask != null)
                    _interiorMask[ex, ey] = true;
            }

            SnapDiagonalElectrodesToGhostLayer();
            UpdateGhostLayer();
        }

        public void ApplyVirtualElectrodes(VirtualElectrodeSettings settings)
        {
            if (settings == null)
                throw new ArgumentNullException(nameof(settings));

            if (_electrodes.Count > 0)
            {
                var idLookup = _electrodes.ToDictionary(e => e.Id);
                foreach (var cell in _elements)
                {
                    if (!cell.IsElectrode || cell.ElectrodeId < 0)
                        continue;

                    if (idLookup.TryGetValue(cell.ElectrodeId, out var electrode) && electrode.IsVirtual)
                    {
                        cell.IsElectrode = false;
                        cell.ElectrodeId = -1;
                    }
                }

                var realElectrodes = _electrodes.Where(e => !e.IsVirtual).OrderBy(e => e.Id).Cast<LBMElectrode>().ToList();
                SetElectrodes(realElectrodes);
            }

            if (!settings.ShouldApplyVirtualElectrodes() || settings.VirtualElectrodesPerGap <= 0 || _electrodes.Count < 2)
                return;

            var boundaryRing = GetBoundaryRing()
                .Where(entry => entry.InteriorCell != null)
                .ToList();
            if (boundaryRing.Count == 0)
            {
                UpdateGhostLayer();
                return;
            }

            var boundaryCells = boundaryRing.Select(p => p.InteriorCell!).ToList();
            var boundaryAngles = boundaryRing.Select(p => p.Angle).ToArray();
            var boundaryLookup = boundaryCells
                .Select((cell, idx) => new { cell.Id, Index = idx })
                .ToDictionary(x => x.Id, x => x.Index);

            var orderedReal = _electrodes.Cast<LBMElectrode>()
                .Select(e => (Electrode: e, Angle: ComputeCellAngle(e.GridId)))
                .OrderBy(entry => entry.Angle)
                .ToList();

            var used = new bool[boundaryCells.Count];
            foreach (var (electrode, _) in orderedReal)
            {
                if (boundaryLookup.TryGetValue(electrode.GridId, out int idx))
                {
                    used[idx] = true;
                    var cell = boundaryCells[idx];
                    cell.IsElectrode = true;
                    cell.ElectrodeId = electrode.Id;
                    if (_interiorMask != null)
                    {
                        var (ix, iy) = ToLattice(cell.Id);
                        _interiorMask[ix, iy] = true;
                    }
                }
            }

            var augmented = new List<LBMElectrode>(_electrodes.Cast<LBMElectrode>());
            int nextId = augmented.Count;
            int perGap = Math.Max(1, settings.VirtualElectrodesPerGap);

            for (int i = 0; i < orderedReal.Count; i++)
            {
                var current = orderedReal[i];
                var next = orderedReal[(i + 1) % orderedReal.Count];
                double span = AngleDelta(current.Angle, next.Angle);

                double leftZ = current.Electrode.ZContact;
                double rightZ = next.Electrode.ZContact;
                double zContact = (double.IsFinite(leftZ) && double.IsFinite(rightZ))
                    ? 0.5 * (leftZ + rightZ)
                    : 0.0;

                for (int k = 0; k < perGap; k++)
                {
                    double fraction = (k + 1.0) / (perGap + 1.0);
                    double targetAngle = NormalizeAngle(current.Angle + span * fraction);
                    int boundaryIndex = FindClosestAvailableBoundaryIndex(targetAngle, boundaryAngles, used);
                    if (boundaryIndex < 0)
                        continue;

                    var cell = boundaryCells[boundaryIndex];
                    used[boundaryIndex] = true;
                    cell.IsElectrode = true;
                    cell.ElectrodeId = nextId;
                    if (_interiorMask != null)
                    {
                        var (ix, iy) = ToLattice(cell.Id);
                        _interiorMask[ix, iy] = true;
                    }

                    var virtualElectrode = new LBMElectrode(
                        id: nextId,
                        gridId: cell.Id,
                        current: 0.0,
                        potential: 0.0,
                        contactImpedance: zContact,
                        isExcitation: false,
                        isGround: false,
                        isMeasuring: true,
                        isVirtual: true);

                    augmented.Add(virtualElectrode);
                    nextId++;
                }
            }

            SetElectrodes(augmented);
        }

        private List<(LBMElement WallCell, LBMElement? InteriorCell, double Angle)> GetBoundaryRing()
        {
            double cx = (Nx - 1) / 2.0;
            double cy = (Ny - 1) / 2.0;
            var boundary = new List<(LBMElement WallCell, LBMElement? InteriorCell, double Angle)>();

            for (int y = 0; y < Ny; y++)
            {
                for (int x = 0; x < Nx; x++)
                {
                    var cell = _grid[x, y];
                    if (!cell.IsWall)
                        continue;

                    bool touchesInterior = false;
                    LBMElement? interiorCandidate = null;
                    double bestDistance = double.MaxValue;
                    for (int k = 1; k < 9; k++)
                    {
                        var neighbor = cell.Neighbors[k];
                        if (neighbor == null)
                            continue;

                        if(!neighbor.IsWall)
                        {
                            touchesInterior = true;
                            var (nx, ny) = ToLattice(neighbor.Id);
                            double dx = nx - cx;
                            double dy = ny - cy;
                            double dist = dx * dx + dy * dy;
                            if (dist < bestDistance)
                            {
                                bestDistance = dist;
                                interiorCandidate = neighbor;
                            }
                        }
                    }

                    if (!touchesInterior)
                        continue;

                    double angle = NormalizeAngle(Math.Atan2(y - cy, x - cx));
                    boundary.Add((cell, interiorCandidate, angle));
                }
            }

            boundary.Sort((a, b) => a.Angle.CompareTo(b.Angle));
            return boundary;
        }

        private void UpdateGhostLayer()
        {
            if (_interiorMask == null)
                return;

            RebuildGhostLayerFromMask();
        }

        private void RebuildBoundaryTopologyFromState()
        {
            if (_elements.Count == 0)
            {
                _boundaryTopology = LBMBoundaryTopology.Empty;
                return;
            }

            var idToIndex = new Dictionary<int, int>(_elements.Count);
            for (int i = 0; i < _elements.Count; i++)
                idToIndex[_elements[i].Id] = i;

            var links = new List<BoundaryLink>();
            var perInterior = new Dictionary<int, List<int>>();
            var existing = new HashSet<(int interior, int direction)>();

            double deltaX_LU = LatticeBoltzmannConstants.DeltaX;
            double deltaXPhys = LBUnitConverter.DeltaXPhys;
            if (!double.IsFinite(deltaXPhys) || deltaXPhys <= 0.0)
                deltaXPhys = deltaX_LU; // Δx_phys must always be positive; fall back to LU spacing otherwise.

            for (int idx = 0; idx < _elements.Count; idx++)
            {
                var element = _elements[idx];
                if (element.GhostElement || element.IsWall)
                    continue;

                for (int dir = 1; dir < NeighborDirections.Length; dir++)
                {
                    bool isDiagonal = dir >= 5;
                    if (isDiagonal && !UseDiagonalBoundaryLinks)
                        continue; // Optional shortcut for regression testing without oblique links.

                    var neighbor = element.Neighbors[dir];
                    if (neighbor is null || !neighbor.GhostElement)
                        continue;

                    if (!idToIndex.TryGetValue(neighbor.Id, out int ghostIndex))
                        continue;

                    if (!existing.Add((idx, dir)))
                        continue; // Deduplicate within each (interior, direction) pair.

                    // Why √2 on diagonals? Each link represents a half-way face whose measure is |c_k| · Δx.
                    // Using per-link Δs ensures the integral of j_n over an electrode arc equals the imposed current.
                    double metricScale = isDiagonal ? Math.Sqrt(2.0) : 1.0;
                    double interfaceLu = deltaX_LU * metricScale;
                    double interfacePhys = deltaXPhys * metricScale;

                    int linkIndex = links.Count;
                    links.Add(new BoundaryLink(
                        idx,
                        ghostIndex,
                        dir,
                        _elements[idx].ElectrodeId,
                        interfaceLu,
                        interfacePhys));

                    if (!perInterior.TryGetValue(idx, out var perCell))
                    {
                        perCell = new List<int>();
                        perInterior[idx] = perCell;
                    }

                    perCell.Add(linkIndex);
                }
            }

            _boundaryTopology = LBMBoundaryTopology.Create(links, perInterior);
        }

        private double ComputeCellAngle(int gridId)
        {
            var (x, y) = ToLattice(gridId);
            double cx = (Nx - 1) / 2.0;
            double cy = (Ny - 1) / 2.0;
            return NormalizeAngle(Math.Atan2(y - cy, x - cx));
        }

        private void SnapDiagonalElectrodesToGhostLayer()
        {
            if (_electrodes.Count == 0)
                return;

            var moved = new List<(int ElectrodeId, int From, int To)>();

            foreach (var electrode in _electrodes.Cast<LBMElectrode>())
            {
                var (ex, ey) = ToLattice(electrode.GridId);
                if (ex < 0 || ex >= Nx || ey < 0 || ey >= Ny)
                    continue;

                var currentCell = _grid[ex, ey];
                if (!IsDiagonalBoundaryContact(currentCell))
                    continue;

                var target = FindNearestCardinalBoundaryCell(ex, ey);
                if (target is null)
                    continue;

                var (tx, ty) = ToLattice(target.Id);

                currentCell.IsElectrode = false;
                currentCell.ElectrodeId = -1;

                electrode.GridId = target.Id;
                target.IsElectrode = true;
                target.ElectrodeId = electrode.Id;

                if (_interiorMask != null)
                {
                    _interiorMask[ex, ey] = !currentCell.IsWall;
                    _interiorMask[tx, ty] = true;
                }

                moved.Add((electrode.Id, currentCell.Id, target.Id));
            }

            foreach (var (electrodeId, from, to) in moved)
                Workspace.AddLogMessage("LBMGrid", $"Snapped diagonal electrode {electrodeId} from cell {from} to boundary cell {to}.");
        }

        private bool IsDiagonalBoundaryContact(LBMElement cell)
        {
            bool touchesCardinalGhost = false;
            bool touchesDiagonalGhost = false;

            for (int dir = 1; dir < NeighborDirections.Length; dir++)
            {
                var neighbor = cell.Neighbors[dir];
                if (neighbor is null || !neighbor.GhostElement)
                    continue;

                if (dir <= 4)
                    touchesCardinalGhost = true;
                else
                    touchesDiagonalGhost = true;
            }

            return touchesDiagonalGhost && !touchesCardinalGhost;
        }

        private LBMElement? FindNearestCardinalBoundaryCell(int startX, int startY)
        {
            var visited = new bool[Nx, Ny];
            var queue = new Queue<(int x, int y)>();

            void Enqueue(int x, int y)
            {
                if (x < 0 || x >= Nx || y < 0 || y >= Ny || visited[x, y])
                    return;

                visited[x, y] = true;
                queue.Enqueue((x, y));
            }

            Enqueue(startX, startY);

            while (queue.Count > 0)
            {
                var (x, y) = queue.Dequeue();
                var cell = _grid[x, y];

                if (IsCardinalBoundaryCell(cell))
                    return cell;

                for (int dir = 1; dir <= 4; dir++)
                {
                    var (dx, dy) = NeighborDirections[dir];
                    Enqueue(x + dx, y + dy);
                }
            }

            return null;
        }

        private bool IsCardinalBoundaryCell(LBMElement cell)
        {
            if (cell.IsWall || cell.GhostElement)
                return false;

            for (int dir = 1; dir <= 4; dir++)
            {
                var neighbor = cell.Neighbors[dir];
                if (neighbor != null && neighbor.GhostElement)
                    return true;
            }

            return false;
        }

        private static double NormalizeAngle(double angle)
        {
            double twoPi = Math.PI * 2.0;
            double result = angle % twoPi;
            if (result < 0)
                result += twoPi;
            return result;
        }

        private static double AngleDelta(double from, double to)
        {
            double delta = NormalizeAngle(to - from);
            if (delta <= 0)
                delta += Math.PI * 2.0;
            return delta;
        }

        private static int FindClosestAvailableBoundaryIndex(double targetAngle, double[] boundaryAngles, bool[] used)
        {
            int bestIndex = -1;
            double bestScore = double.MaxValue;
            double twoPi = Math.PI * 2.0;

            for (int i = 0; i < boundaryAngles.Length; i++)
            {
                if (used[i])
                    continue;

                double diff = Math.Abs(boundaryAngles[i] - targetAngle);
                diff = Math.Min(diff, twoPi - diff);
                if (diff < bestScore)
                {
                    bestScore = diff;
                    bestIndex = i;
                }
            }

            return bestIndex;
        }

        public Dictionary<int, double> GetElectrodeAngles()
        {
            var angles = new Dictionary<int, double>(_electrodes.Count);
            foreach (var electrode in _electrodes.Cast<LBMElectrode>())
                angles[electrode.Id] = ComputeCellAngle(electrode.GridId);
            return angles;
        }

        public void RebuildGrid()
        {
            for (int x = 0; x < Nx; x++)
            {
                for (int y = 0; y < Ny; y++)
                {
                    int id = x * Nx + y;
                    var correspondingElement = _elements.Find(e => e.Id == id)
                        ?? throw new InvalidOperationException("Cannot set grid, element id mismatch. The ids should be at top left, and descend to bottom right. Check stored data!");
                    _grid[x, y] = correspondingElement;
                }
            }

            _interiorMask = new bool[Nx, Ny];
            for (int y = 0; y < Ny; y++)
                for (int x = 0; x < Nx; x++)
                    _interiorMask[x, y] = !_grid[x, y].IsWall;

            RebuildGhostLayerFromMask();
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
        public override void LogDiscretization()
        {
            Console.WriteLine($"LBM | {Nx}x{Ny}, E={_elements.Count}, EL={_electrodes.Count}");
        }

        public override Discretization DeepCopy()
        {
            var copy = new LBMGrid(Nx, Ny)
            {
                Metadata = new DiscretizationMetaData
                {
                    CreatedOn = this.Metadata.CreatedOn,
                    Generator = this.Metadata.Generator,
                    Parameters = new Dictionary<string, string>(this.Metadata.Parameters)
                }
            };

            // copy element state
            for (int i = 0; i < _elements.Count; i++)
            {
                copy.ElementsTyped[i].Conductivity = _elements[i].Conductivity;
                var src = _elements[i];
                var dst = copy.ElementsTyped[i];

                dst.Conductivity = src.Conductivity;
                dst.IsWall = src.IsWall;
                dst.GhostElement = src.GhostElement;
                dst.IsElectrode = src.IsElectrode;
                dst.ElectrodeId = src.ElectrodeId;

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
                    isMeasuring: e.IsMeasuring,
                    isVirtual: e.IsVirtual)).ToList();

            List<LBMElectrode> electrodesTyped = [];

            foreach(var el in electrodes)
                electrodesTyped.Add(new LBMElectrode(
                    id: el.Id,
                    gridId: el.GridId,
                    current: el.Current,
                    potential: el.Potential,
                    contactImpedance: el.ZContact,
                    isExcitation: el.IsExcitation,
                    isGround: el.IsGround,
                    isMeasuring: el.IsMeasuring,
                    isVirtual: el.IsVirtual));

            copy.SetElectrodes(electrodesTyped);

            // clone distributions
            var cd = copy.GetElements()
                          .Cast<LBMElement>()
                          .ToDictionary(el => el.Id, el => el.Conductivity);
            copy.SetConductivityDistribution(new ConductivityDistribution(cd));

            var pd = copy.GetElements()
                          .Cast<LBMElement>()
                          .ToDictionary(el => el.Id, el => el.Fi.Sum());
            copy.SetPotentialDistribution(new PotentialDistribution(pd));

            copy.SetElements([.. copy.ElementsTyped]);

            copy.RebuildBoundaryTopologyFromState();

            return copy;
        }

        /// <summary>
        /// Uniform grid upsampling by an integer factor (e.g., 2 = each coarse cell becomes 2x2 fine cells).
        /// Conductivity is replicated; potentials are copied.
        /// Electrodes are re-centered inside the corresponding refined block.
        /// </summary>
        public override LBMGrid RefineUniform(int factor = 2)
        {
            if (factor <= 1) return (LBMGrid)this.DeepCopy();

            int NX = Nx * factor;
            int NY = Ny * factor;
            var fine = new LBMGrid(NX, NY);

            // Map conductivity/potential
            for (int y = 0; y < Ny; y++)
                for (int x = 0; x < Nx; x++)
                {
                    var src = _grid[x, y];
                    for (int fy = y * factor; fy < (y + 1) * factor; fy++)
                    for (int fx = x * factor; fx < (x + 1) * factor; fx++)
                        {
                            var dst = fine._grid[fx, fy];
                            bool replicateWall = src.IsWall && !src.GhostElement;
                            if (replicateWall)
                            {
                                bool srcOnOuterBoundary = x == 0 || x == Nx - 1 || y == 0 || y == Ny - 1;
                                dst.IsWall = srcOnOuterBoundary
                                    ? (fx == 0 || fy == 0 || fx == NX - 1 || fy == NY - 1)
                                    : true;
                            }
                            else
                            {
                                dst.IsWall = false;
                            }
                            dst.GhostElement = false;
                            dst.Conductivity = src.Conductivity;

                            double pot = src.Fi.Sum();
                            double eq = pot / 9.0;
                            for (int k = 0; k < 9; k++) dst.Fi[k] = eq;
                        }
                }

            // Recreate electrodes on the refined boundary by matching angular positions
            var boundaryRing = fine.GetBoundaryRing()
                .Where(entry => entry.InteriorCell != null)
                .ToList();
            var boundaryAngles = boundaryRing.Select(p => p.Angle).ToArray();
            var used = new bool[boundaryAngles.Length];
            var newElectrodes = new List<LBMElectrode>();

            foreach (var electrode in _electrodes.Cast<LBMElectrode>())
            {
                if (boundaryAngles.Length == 0)
                    break;

                double targetAngle = ComputeCellAngle(electrode.GridId);
                int idx = FindClosestAvailableBoundaryIndex(targetAngle, boundaryAngles, used);
                if (idx < 0)
                {
                    idx = Array.FindIndex(used, flag => !flag);
                    if (idx < 0)
                        break;
                }

                used[idx] = true;
                var cell = boundaryRing[idx].InteriorCell!;
                cell.IsElectrode = true;
                cell.ElectrodeId = electrode.Id;

                newElectrodes.Add(new LBMElectrode(
                    id: electrode.Id,
                    gridId: cell.Id,
                    current: electrode.Current,
                    potential: electrode.Potential,
                    contactImpedance: electrode.ZContact,
                    isExcitation: electrode.IsExcitation,
                    isGround: electrode.IsGround,
                    isMeasuring: electrode.IsMeasuring,
                    isVirtual: electrode.IsVirtual));
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

            var verts = new List<GraphFEMVertex>();
            var idToVtx = new Dictionary<int, GraphFEMVertex>();

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

                    var gv = new GraphFEMVertex(x, y, cell.Id, domainId: 0, boundaryId: boundaryId)
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

        public override LBMGrid FromGraph(GraphMesh.Graph graphToConvert)
        {
            if (graphToConvert == null) throw new ArgumentNullException(nameof(graphToConvert));
            if (graphToConvert.Vertices.Count == 0)
                throw new InvalidOperationException("Graph has no vertices.");

            // Infer a rectangular interior grid from graph FEMVertex (x,y) coords.
            // We assume the graph came from ToGraph(): integer coords for interior cells only.
            int minX = (int)Math.Round(graphToConvert.Vertices.Min(v => v.X));
            int maxX = (int)Math.Round(graphToConvert.Vertices.Max(v => v.X));
            int minY = (int)Math.Round(graphToConvert.Vertices.Min(v => v.Y));
            int maxY = (int)Math.Round(graphToConvert.Vertices.Max(v => v.Y));

            // Add a 1-cell wall border around interior domain
            int NX = (maxX - minX + 1) + 2;
            int NY = (maxY - minY + 1) + 2;

            var mesh = new LBMGrid(NX, NY);

            // Map graph FEMVertex -> new grid cell index (shift by +1,+1 due to wall border)
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