using System.Collections.Generic;
using Utility.Classes.Discretizer;
using Utility.Classes.Discretizer.FiniteElementMesh;
using Utility.Classes.Discretizer.LatticeBoltzmannGrid;

namespace Utility.Classes.PostProcessing
{
    internal static class PostProcessingHelpers
    {
        public static Dictionary<int, List<int>> BuildElementNeighbors(IDiscretization discretization)
        {
            if (discretization is FEMMesh femMesh)
                return BuildFemNeighbors(femMesh);

            if (discretization is LBMGrid lbmGrid)
                return BuildLbmNeighbors(lbmGrid);

            // Fallback: no topology information available
            return [];
        }

        private static Dictionary<int, List<int>> BuildFemNeighbors(FEMMesh mesh)
        {
            var neighbors = new Dictionary<int, List<int>>();
            var elements = mesh.ElementsTyped;

            var vertexToElement = new Dictionary<int, List<int>>();
            foreach (var element in elements)
            {
                foreach (var vertex in element.Vertices)
                {
                    if (!vertexToElement.TryGetValue(vertex.GlobalId, out var list))
                    {
                        list = new List<int>();
                        vertexToElement[vertex.GlobalId] = list;
                    }

                    list.Add(element.Id);
                }
            }

            foreach (var element in elements)
            {
                if (!neighbors.ContainsKey(element.Id))
                    neighbors[element.Id] = new List<int>();

                var adjacency = neighbors[element.Id];
                foreach (var vertex in element.Vertices)
                {
                    if (!vertexToElement.TryGetValue(vertex.GlobalId, out var sharingElements))
                        continue;

                    foreach (var otherId in sharingElements)
                    {
                        if (otherId == element.Id)
                            continue;
                        if (!adjacency.Contains(otherId))
                            adjacency.Add(otherId);
                    }
                }
            }

            return neighbors;
        }

        private static Dictionary<int, List<int>> BuildLbmNeighbors(LBMGrid grid)
        {
            var neighbors = new Dictionary<int, List<int>>(grid.Nx * grid.Ny);

            foreach (var element in grid.GetElements())
            {
                if (element is not LBMElement cell)
                    continue;

                var list = new List<int>();
                var (x, y) = grid.ToLattice(cell.Id);

                void TryAdd(int nx, int ny)
                {
                    if (nx < 0 || ny < 0 || nx >= grid.Nx || ny >= grid.Ny)
                        return;

                    var neighbor = grid.GetElementAt(nx, ny);
                    if (neighbor.IsWall)
                        return;

                    list.Add(neighbor.Id);
                }

                TryAdd(x - 1, y);
                TryAdd(x + 1, y);
                TryAdd(x, y - 1);
                TryAdd(x, y + 1);

                neighbors[cell.Id] = list;
            }

            return neighbors;
        }
    }
}
