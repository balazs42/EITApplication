using System;
using System.Collections.Generic;
using System.Linq;
using Utility.Classes.Meshing.GraphMesh;
using Utility.Classes.Meshing.Graph.Graph;

namespace Utility.Classes.Factories
{
    /// <summary>
    /// Factory for creating simple circular graphs used by graph-based solvers.
    /// </summary>
    public static class GraphFactory
    {
        /// <summary>
        ///     Creates a circular boundary-only graph with unit edge weights.
        ///     Each vertex lies on the unit circle at angle <c>2π i / N</c> and
        ///     edges connect consecutive boundary vertices.  This graph
        ///     corresponds to a discrete approximation of a conducting ring
        ///     where all conductances are initially set to one.
        /// </summary>
        /// <param name="boundaryCount">
        ///     Number of boundary nodes / electrodes around the circle.
        /// </param>
        /// <returns>A <see cref="Graph"/> representing a uniform circular network.</returns>
        public static Graph CreateCircular(int boundaryCount)
        {
            if (boundaryCount < 3)
                throw new ArgumentOutOfRangeException(nameof(boundaryCount));

            var vertices = new List<GraphFEMVertex>(boundaryCount);
            for (int i = 0; i < boundaryCount; i++)
            {
                double angle = 2.0 * Math.PI * i / boundaryCount;
                vertices.Add(new GraphFEMVertex(Math.Cos(angle), Math.Sin(angle), i, 0, i + 1));
            }

            var edges = new List<GraphEdge>(boundaryCount);
            for (int i = 0; i < boundaryCount; i++)
            {
                var v1 = vertices[i];
                var v2 = vertices[(i + 1) % boundaryCount];
                edges.Add(new GraphEdge(v1, v2, 1.0));
            }

            return new Graph(vertices, edges);
        }
    }
}
