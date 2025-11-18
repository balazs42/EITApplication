using System.Collections.Generic;
using System.Collections.ObjectModel;

namespace Utility.Classes.Discretizer.LatticeBoltzmannGrid
{
    public readonly struct LBMBoundaryLink
    {
        public LBMBoundaryLink(
            int interiorIndex,
            int ghostIndex,
            int direction,
            double interfaceLengthLu,
            double interfaceLengthPhys,
            int electrodeId)
        {
            InteriorIndex = interiorIndex;
            GhostIndex = ghostIndex;
            Direction = direction;
            InterfaceLengthLU = interfaceLengthLu;
            InterfaceLengthPhys = interfaceLengthPhys;
            ElectrodeId = electrodeId;
        }

        public int InteriorIndex { get; }
        public int GhostIndex { get; }
        public int Direction { get; }

        /// <summary>
        /// Interface measure Δs expressed in lattice units (LU).  Axis links carry Δx
        /// while diagonal links carry √2 Δx so that Neumann fluxes integrate correctly.
        /// </summary>
        public double InterfaceLengthLU { get; }

        /// <summary>
        /// Interface measure Δs expressed in physical units (SI).  Falls back to the LU
        /// value when the simulation operates entirely in lattice space.
        /// </summary>
        public double InterfaceLengthPhys { get; }

        /// <summary>
        /// Identifier of the electrode that owns this boundary link.  Links attached to insulating
        /// portions of the boundary carry -1.  The field lets boundary-condition assembly attribute
        /// flux contributions without having to re-run topological searches.
        /// </summary>
        public int ElectrodeId { get; }
    }

    public sealed class LBMBoundaryTopology
    {
        private static readonly IReadOnlyList<LBMBoundaryLink> EmptyLinks = new List<LBMBoundaryLink>().AsReadOnly();
        private static readonly IReadOnlyDictionary<int, IReadOnlyList<int>> EmptyLookup =
            new ReadOnlyDictionary<int, IReadOnlyList<int>>(new Dictionary<int, IReadOnlyList<int>>());

        public static LBMBoundaryTopology Empty { get; } = new(EmptyLinks, EmptyLookup);

        private LBMBoundaryTopology(
            IReadOnlyList<LBMBoundaryLink> links,
            IReadOnlyDictionary<int, IReadOnlyList<int>> linksByInterior)
        {
            Links = links;
            LinksByInterior = linksByInterior;
        }

        public IReadOnlyList<LBMBoundaryLink> Links { get; }
        public IReadOnlyDictionary<int, IReadOnlyList<int>> LinksByInterior { get; }

        internal static LBMBoundaryTopology Create(
            List<LBMBoundaryLink> links,
            Dictionary<int, List<int>> perInterior)
        {
            if (links.Count == 0)
                return Empty;

            var readOnlyLinks = new ReadOnlyCollection<LBMBoundaryLink>(links.ToArray());
            var lookup = new Dictionary<int, IReadOnlyList<int>>(perInterior.Count);
            foreach (var kvp in perInterior)
                lookup[kvp.Key] = new ReadOnlyCollection<int>(kvp.Value.ToArray());

            return new LBMBoundaryTopology(readOnlyLinks, new ReadOnlyDictionary<int, IReadOnlyList<int>>(lookup));
        }
    }
}
