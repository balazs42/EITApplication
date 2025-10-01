using System.Collections.Generic;
using System.Linq;
using Utility.Classes.Discretizer.LatticeBoltzmannGrid;

namespace Utility.Classes.Solvers.LatticeBoltzmannSolver
{
    internal sealed class LatticeBoltzmannHostTopology
    {
        public LBMElement[] Elements { get; }
        public int[] ElementIds { get; }
        public int[] NeighborIndices { get; }
        public int[] NeighborIsWall { get; }
        public int[] IsWall { get; }
        public Dictionary<int, int> IdToIndex { get; }

        public int ElementCount => Elements.Length;

        public LatticeBoltzmannHostTopology(
            LBMElement[] elements,
            int[] elementIds,
            int[] neighborIndices,
            int[] neighborIsWall,
            int[] isWall,
            Dictionary<int, int> idToIndex)
        {
            Elements = elements;
            ElementIds = elementIds;
            NeighborIndices = neighborIndices;
            NeighborIsWall = neighborIsWall;
            IsWall = isWall;
            IdToIndex = idToIndex;
        }
    }

    internal static class LatticeBoltzmannCudaHelper
    {
        public static LatticeBoltzmannHostTopology BuildTopology(LBMGrid grid)
        {
            var elements = grid.GetElements().Cast<LBMElement>().ToArray();
            int count = elements.Length;

            var idToIndex = new Dictionary<int, int>(count);
            var elementIds = new int[count];
            for (int i = 0; i < count; i++)
            {
                int id = elements[i].Id;
                elementIds[i] = id;
                idToIndex[id] = i;
            }

            var neighborIndices = new int[count * 9];
            var neighborIsWall = new int[count * 9];
            var isWall = new int[count];

            for (int i = 0; i < count; i++)
            {
                var element = elements[i];
                isWall[i] = element.IsWall ? 1 : 0;

                for (int k = 0; k < 9; k++)
                {
                    var neighbor = element.Neighbors[k];
                    int arrayIndex = i * 9 + k;

                    if (neighbor != null && idToIndex.TryGetValue(neighbor.Id, out var neighborIndex))
                    {
                        neighborIndices[arrayIndex] = neighborIndex;
                        neighborIsWall[arrayIndex] = neighbor.IsWall ? 1 : 0;
                    }
                    else
                    {
                        neighborIndices[arrayIndex] = -1;
                        neighborIsWall[arrayIndex] = 0;
                    }
                }
            }

            return new LatticeBoltzmannHostTopology(
                elements,
                elementIds,
                neighborIndices,
                neighborIsWall,
                isWall,
                idToIndex);
        }
    }
}
