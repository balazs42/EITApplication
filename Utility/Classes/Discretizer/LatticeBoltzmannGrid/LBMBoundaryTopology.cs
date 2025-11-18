using System.Collections.Generic;
using System.Collections.ObjectModel;

namespace Utility.Classes.Discretizer.LatticeBoltzmannGrid
{
    /// <summary>
    /// Describes a single link between an interior fluid cell and its paired ghost element.
    /// Each link tracks the owning electrode (if any) together with the geometric interface
    /// measure Δs expressed both in lattice units (LU) and in physical SI units.
    /// </summary>
    internal readonly struct BoundaryLink
    {
        public BoundaryLink(
            int interiorIndex,
            int ghostIndex,
            int direction,
            int electrodeId,
            double interfaceLengthLu,
            double interfaceLengthPhys)
        {
            InteriorIndex = interiorIndex;
            GhostIndex = ghostIndex;
            Direction = direction;
            ElectrodeId = electrodeId;
            InterfaceLengthLU = interfaceLengthLu;
            InterfaceLengthPhys = interfaceLengthPhys;
        }

        public int InteriorIndex { get; }
        public int GhostIndex { get; }
        public int Direction { get; }
        public int ElectrodeId { get; }

        /// <summary>
        /// Interface measure Δs expressed in lattice units.  Diagonal links are scaled by √2 to
        /// reflect the longer intersection length with the physical boundary.
        /// </summary>
        public double InterfaceLengthLU { get; }

        /// <summary>
        /// Interface measure Δs expressed in SI units.  The value mirrors the LU metric via the
        /// configured Δx_phys so that flux densities integrate consistently in either unit system.
        /// </summary>
        public double InterfaceLengthPhys { get; }
    }

    internal sealed class LBMBoundaryTopology
    {
        private static readonly IReadOnlyList<BoundaryLink> EmptyLinks = new List<BoundaryLink>().AsReadOnly();
        private static readonly IReadOnlyDictionary<int, IReadOnlyList<int>> EmptyLookup =
            new ReadOnlyDictionary<int, IReadOnlyList<int>>(new Dictionary<int, IReadOnlyList<int>>());

        public static LBMBoundaryTopology Empty { get; } = new(EmptyLinks, EmptyLookup);

        private LBMBoundaryTopology(
            IReadOnlyList<BoundaryLink> links,
            IReadOnlyDictionary<int, IReadOnlyList<int>> linksByInterior)
        {
            Links = links;
            LinksByInterior = linksByInterior;
        }

        public IReadOnlyList<BoundaryLink> Links { get; }
        public IReadOnlyDictionary<int, IReadOnlyList<int>> LinksByInterior { get; }

        internal static LBMBoundaryTopology Create(
            List<BoundaryLink> links,
            Dictionary<int, List<int>> perInterior)
        {
            if (links.Count == 0)
                return Empty;

            var readOnlyLinks = new ReadOnlyCollection<BoundaryLink>(links.ToArray());
            var lookup = new Dictionary<int, IReadOnlyList<int>>(perInterior.Count);
            foreach (var kvp in perInterior)
                lookup[kvp.Key] = new ReadOnlyCollection<int>(kvp.Value.ToArray());

            return new LBMBoundaryTopology(readOnlyLinks, new ReadOnlyDictionary<int, IReadOnlyList<int>>(lookup));
        }
    }
}
