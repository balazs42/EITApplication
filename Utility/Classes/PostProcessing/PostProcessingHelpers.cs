using System.Collections.Generic;
using Utility.Classes.Discretizer;
using Utility.Classes.Discretizer.FiniteElementMesh;

namespace Utility.Classes.PostProcessing
{
    internal static class PostProcessingHelpers
    {
        public static Dictionary<int, List<int>> BuildElementNeighbors(IDiscretization discretization)
        {
            if (discretization is FEMMesh femMesh)
                return BuildFemNeighbors(femMesh);

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
    }
}
