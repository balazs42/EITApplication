using System.Collections.Generic;
using System.Linq;
using Utility.Classes.Discretizer.LatticeBoltzmannGrid;

namespace Utility.Classes.Solvers.LatticeBoltzmannSolver
{
    /// <summary>
    /// Data structure containing flattened LBM grid topology optimized for GPU execution.
    /// Converts object-oriented mesh structure into flat arrays for efficient CUDA kernel access.
    /// </summary>
    internal sealed class LatticeBoltzmannHostTopology
    {
        /// <summary>
        /// Array of LBM elements in linear order for GPU indexing.
        /// Each element contains distribution functions and material properties.
        /// </summary>
        public LBMElement[] Elements { get; }
        
        /// <summary>
        /// Array of element IDs corresponding to Elements array indices.
        /// Maintains mapping between linear GPU indices and original mesh IDs.
        /// </summary>
        public int[] ElementIds { get; }
        
        /// <summary>
        /// Flattened neighbor indices for all elements and all 9 D2Q9 directions.
        /// Layout: [elem0_dir0, elem0_dir1, ..., elem0_dir8, elem1_dir0, ...]
        /// Value -1 indicates no neighbor (boundary or outside domain).
        /// </summary>
        public int[] NeighborIndices { get; }

        /// <summary>
        /// Flattened array indicating if each neighbor is a wall for bounce-back.
        /// Same layout as NeighborIndices: 1=wall, 0=fluid.
        /// Used by streaming kernel to determine bounce-back behavior.
        /// </summary>
        public int[] NeighborIsWall { get; }

        /// <summary>
        /// Flattened array indicating whether a neighbor is a ghost node (1) or not (0).
        /// The ghost layer carries Neumann boundary information for electrodes.
        /// </summary>
        public int[] NeighborIsGhost { get; }
        
        /// <summary>
        /// Array indicating which elements are walls (1) or fluid (0).
        /// Wall elements don't participate in collision or streaming operations.
        /// </summary>
        public int[] IsWall { get; }

        /// <summary>
        /// Array indicating which elements belong to the ghost layer (1) or the physical domain (0).
        /// </summary>
        public int[] IsGhost { get; }
        
        /// <summary>
        /// Dictionary mapping original element IDs to linear array indices.
        /// Enables fast lookup when converting between mesh and GPU representations.
        /// </summary>
        public Dictionary<int, int> IdToIndex { get; }

        /// <summary>
        /// Total number of elements in the flattened topology.
        /// Determines GPU kernel launch parameters and memory allocation sizes.
        /// </summary>
        public int ElementCount => Elements.Length;

        /// <summary>
        /// Constructor initializing all topology data structures.
        /// Called only by LatticeBoltzmannCudaHelper.BuildTopology().
        /// </summary>
        public LatticeBoltzmannHostTopology(
            LBMElement[] elements,
            int[] elementIds,
            int[] neighborIndices,
            int[] neighborIsWall,
            int[] neighborIsGhost,
            int[] isWall,
            int[] isGhost,
            Dictionary<int, int> idToIndex)
        {
            Elements = elements;
            ElementIds = elementIds;
            NeighborIndices = neighborIndices;
            NeighborIsWall = neighborIsWall;
            NeighborIsGhost = neighborIsGhost;
            IsWall = isWall;
            IsGhost = isGhost;
            IdToIndex = idToIndex;
        }
    }

    /// <summary>
    /// Helper class converting object-oriented LBM grid into flat arrays for GPU computation.
    /// Transforms irregular mesh connectivity into regular array indexing for efficient CUDA kernels.
    /// </summary>
    internal static class LatticeBoltzmannCudaHelper
    {
        /// <summary>
        /// Converts LBMGrid object into flattened topology suitable for GPU kernels.
        /// Linearizes neighbor relationships and creates index mappings for efficient GPU access.
        /// </summary>
        /// <param name="grid">Input LBM grid with object-oriented structure</param>
        /// <returns>Flattened topology with arrays optimized for GPU computation</returns>
        public static LatticeBoltzmannHostTopology BuildTopology(LBMGrid grid)
        {
            // Convert collection of LBM elements to array for linear indexing
            var elements = grid.GetElements().Cast<LBMElement>().ToArray();
            int count = elements.Length;

            // Create bidirectional mapping between element IDs and array indices
            // This enables fast conversion between mesh coordinates and GPU indices
            var idToIndex = new Dictionary<int, int>(count);
            var elementIds = new int[count];
            
            // Build ID-to-index mapping for O(1) neighbor lookups
            for (int i = 0; i < count; i++)
            {
                int id = elements[i].Id; // Original mesh element ID
                elementIds[i] = id;      // Store ID at linear index i
                idToIndex[id] = i;       // Map ID back to linear index
            }

            // Allocate flattened arrays for neighbor connectivity (9 directions per element)
            var neighborIndices = new int[count * 9];    // Neighbor linear indices
            var neighborIsWall = new int[count * 9];     // Neighbor wall flags
            var neighborIsGhost = new int[count * 9];    // Neighbor ghost flags
            var isWall = new int[count];                 // Current element wall flags
            var isGhost = new int[count];                // Current element ghost flags

            // Process each element to build flattened neighbor arrays
            for (int i = 0; i < count; i++)
            {
                var element = elements[i];
                
                // Convert boolean wall flag to integer for GPU compatibility
                isWall[i] = element.IsWall ? 1 : 0;
                isGhost[i] = element.GhostElement ? 1 : 0;

                // Process all 9 D2Q9 directions for current element
                for (int k = 0; k < 9; k++)
                {
                    var neighbor = element.Neighbors[k]; // Get neighbor in direction k
                    int arrayIndex = i * 9 + k;         // Flatten 2D indexing to 1D

                    // Check if neighbor exists and is in our element list
                    if (neighbor != null && idToIndex.TryGetValue(neighbor.Id, out var neighborIndex))
                    {
                        // Valid neighbor found - store its linear index
                        neighborIndices[arrayIndex] = neighborIndex;
                        
                        // Store neighbor's wall status for bounce-back decisions
                        neighborIsWall[arrayIndex] = neighbor.IsWall ? 1 : 0;
                        neighborIsGhost[arrayIndex] = neighbor.GhostElement ? 1 : 0;
                    }
                    else
                    {
                        // No neighbor (domain boundary) - mark as invalid
                        neighborIndices[arrayIndex] = -1; // Invalid index sentinel
                        neighborIsWall[arrayIndex] = 0;   // Not a wall (boundary)
                        neighborIsGhost[arrayIndex] = 0;
                    }
                }
            }

            // Return complete flattened topology ready for GPU transfer
            return new LatticeBoltzmannHostTopology(
                elements,          // Original element objects
                elementIds,        // Element ID array
                neighborIndices,   // Flattened neighbor indices
                neighborIsWall,    // Flattened neighbor wall flags
                neighborIsGhost,  // Flattened neighbor ghost flags
                isWall,           // Element wall flags
                isGhost,          // Element ghost flags
                idToIndex);       // ID-to-index mapping dictionary
        }
    }
}
