using System.Collections.Generic;
using Utility.Classes.Discretizer.LatticeBoltzmannGrid;

namespace Utility.Classes.Solvers.LatticeBoltzmannSolver;

internal sealed class LatticeBoltzmannGpuTopology
{
    public LatticeBoltzmannGpuTopology(
        LBMElement[] elements,
        Dictionary<int, int> idToIndex,
        int[] neighborIndices,
        byte[] neighborExists,
        byte[] neighborIsWall,
        byte[] elementIsWall)
    {
        Elements = elements;
        IdToIndex = idToIndex;
        NeighborIndices = neighborIndices;
        NeighborExists = neighborExists;
        NeighborIsWall = neighborIsWall;
        ElementIsWall = elementIsWall;
    }

    public LBMElement[] Elements { get; }
    public Dictionary<int, int> IdToIndex { get; }
    public int[] NeighborIndices { get; }
    public byte[] NeighborExists { get; }
    public byte[] NeighborIsWall { get; }
    public byte[] ElementIsWall { get; }
}

internal static class LatticeBoltzmannGpuHelper
{
    public static LatticeBoltzmannGpuTopology BuildTopology(LBMGrid mesh)
    {
        var elements = mesh.GetElements().Cast<LBMElement>().ToArray();
        var idToIndex = new Dictionary<int, int>(elements.Length);
        for (int i = 0; i < elements.Length; i++)
            idToIndex[elements[i].Id] = i;

        var neighborIndices = new int[elements.Length * 9];
        var neighborExists = new byte[elements.Length * 9];
        var neighborIsWall = new byte[elements.Length * 9];
        var elementIsWall = new byte[elements.Length];

        for (int i = 0; i < elements.Length; i++)
        {
            var element = elements[i];
            elementIsWall[i] = element.IsWall ? (byte)1 : (byte)0;
            for (int k = 0; k < 9; k++)
            {
                int offset = i * 9 + k;
                var neighbor = element.Neighbors[k];
                if (neighbor != null)
                {
                    neighborIndices[offset] = idToIndex[neighbor.Id];
                    neighborExists[offset] = 1;
                    neighborIsWall[offset] = neighbor.IsWall ? (byte)1 : (byte)0;
                }
                else
                {
                    neighborIndices[offset] = i;
                    neighborExists[offset] = 0;
                    neighborIsWall[offset] = 0;
                }
            }
        }

        return new LatticeBoltzmannGpuTopology(
            elements,
            idToIndex,
            neighborIndices,
            neighborExists,
            neighborIsWall,
            elementIsWall);
    }
}
