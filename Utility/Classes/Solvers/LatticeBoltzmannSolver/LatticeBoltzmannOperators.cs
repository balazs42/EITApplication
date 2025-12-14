using ILGPU;
using ILGPU.Runtime;
using Utility.Classes.Discretizer.LatticeBoltzmannGrid;
using Utility.Classes.Solvers;

namespace Utility.Classes.Solvers.LatticeBoltzmannSolver
{
    /// <summary>
    /// GPU-accelerated differential operators for LBM-based numerical analysis.
    /// Provides CUDA implementations of gradient, Laplacian, and divergence operators
    /// on structured D2Q9 lattice grids using finite difference approximations.
    /// Used for post-processing LBM solutions and inverse problem computations.
    /// </summary>
    public static class LatticeBoltzmannOperators
    {
        // Thread-safe kernel compilation management
        private static readonly object _cudaKernelLock = new();
        
        // Pre-compiled CUDA kernels for differential operators
        private static Action<Index1D, ArrayView<double>, ArrayView<int>, ArrayView<int>, ArrayView<double>, ArrayView<double>>? _gradientKernel;
        private static Action<Index1D, ArrayView<double>, ArrayView<int>, ArrayView<int>, ArrayView<double>>? _laplacianKernel;
        private static Action<Index1D, ArrayView<double>, ArrayView<double>, ArrayView<int>, ArrayView<int>, ArrayView<double>>? _divergenceKernel;

        /// <summary>
        /// Relation (a) from Krüger et al.: D = c_s^2 (τ − 1/2) Δt.  With Δt_LU = 1 the conversion
        /// simplifies to τ = D / c_s^2 + 1/2.  Centralising this helper ensures the CPU and CUDA paths
        /// use identical relaxation times for a given diffusivity.
        /// </summary>
        internal static double ComputeTauFromDiffusivityLU(double diffusivityLu)
            => diffusivityLu / LatticeBoltzmannConstants.CsSquared + 0.5;

        /// <summary>
        /// CPU implementation of central difference gradient operator on D2Q9 mesh.
        /// Computes ∇φ = (∂φ/∂x, ∂φ/∂y) using neighboring values in cardinal directions.
        /// Handles boundary conditions by using one-sided differences at domain edges.
        /// </summary>
        /// <param name="mesh">LBM grid with D2Q9 connectivity</param>
        /// <param name="φ">Scalar field to differentiate</param>
        /// <returns>Vector field containing gradient components</returns>
        public static VectorField CalculateGradient(LBMGrid mesh, ScalarField φ)
        {
            var grad = new Dictionary<int, (double X, double Y)>();
            
            // Process each element in the mesh
            foreach (LBMElement el in mesh.GetElements().Cast<LBMElement>())
            {
                // Get neighbors in cardinal directions (D2Q9 indices 1-4)
                var R = el.Neighbors[1];    // Right neighbor (+X direction)
                var U = el.Neighbors[2];    // Up neighbor (+Y direction)
                var L = el.Neighbors[3];    // Left neighbor (-X direction)
                var D = el.Neighbors[4];    // Down neighbor (-Y direction)

                // Get field values at current and neighboring points
                double φ0 = φ.GetValue(el.Id);  // Center value
                
                // Use neighbor values if available and not wall, otherwise use center value
                double φr = R != null && !R.IsWall ? φ.GetValue(R.Id) : φ0;
                double φl = L != null && !L.IsWall ? φ.GetValue(L.Id) : φ0;
                double φu = U != null && !U.IsWall ? φ.GetValue(U.Id) : φ0;
                double φd = D != null && !D.IsWall ? φ.GetValue(D.Id) : φ0;

                // Compute central differences with appropriate normalization
                // Use central difference if both neighbors exist, otherwise one-sided
                double dx = (φr - φl) / (R != null && L != null ? 2.0 : 1.0);
                double dy = (φu - φd) / (U != null && D != null ? 2.0 : 1.0);

                // Store gradient components for this element
                grad[el.Id] = (dx, dy);
            }

            return new VectorField(grad);
        }

        /// <summary>
        /// GPU-accelerated gradient computation using CUDA kernels.
        /// Parallelizes gradient calculation across all mesh elements for performance.
        /// </summary>
        /// <param name="mesh">LBM grid with flattened topology</param>
        /// <param name="φ">Scalar field to differentiate</param>
        /// <returns>Vector field containing gradient components</returns>
        public static VectorField CalculateGradientCuda(LBMGrid mesh, ScalarField φ)
        {
            // Convert mesh to GPU-optimized topology
            var topology = LatticeBoltzmannCudaHelper.BuildTopology(mesh);
            int count = topology.ElementCount;
            
            // Handle empty mesh case
            if (count == 0)
                return new VectorField(new Dictionary<int, (double, double)>());

            // Ensure CUDA kernels are compiled
            EnsureCudaKernels();

            // Prepare field values in linear array format for GPU
            var fieldValues = new double[count];
            for (int i = 0; i < count; i++)
                fieldValues[i] = φ.GetValue(topology.ElementIds[i]);

            // Get GPU accelerator and allocate device memory
            var accelerator = LatticeBoltzmannCudaContext.Accelerator;

            // Allocate GPU buffers for computation
            using var fieldBuffer = accelerator.Allocate1D<double>(count);           // Input field values
            using var gradXBuffer = accelerator.Allocate1D<double>(count);           // Output X-gradient
            using var gradYBuffer = accelerator.Allocate1D<double>(count);           // Output Y-gradient
            using var neighborIndexBuffer = accelerator.Allocate1D<int>(count * 9);  // Neighbor connectivity
            using var neighborIsWallBuffer = accelerator.Allocate1D<int>(count * 9); // Neighbor wall flags

            // Copy data from CPU to GPU
            fieldBuffer.CopyFromCPU(fieldValues);
            neighborIndexBuffer.CopyFromCPU(topology.NeighborIndices);
            neighborIsWallBuffer.CopyFromCPU(topology.NeighborIsWall);

            // Execute gradient kernel on GPU
            if (_gradientKernel == null)
                throw new NullReferenceException();

            _gradientKernel(count,                      // Number of elements to process
                fieldBuffer.View,                       // Input scalar field
                neighborIndexBuffer.View,               // Neighbor connectivity
                neighborIsWallBuffer.View,              // Neighbor wall flags
                gradXBuffer.View,                       // Output X-gradient
                gradYBuffer.View);                      // Output Y-gradient

            // Wait for GPU computation to complete
            accelerator.Synchronize();
            
            // Copy results back to CPU
            var gradXHost = gradXBuffer.GetAsArray1D();
            var gradYHost = gradYBuffer.GetAsArray1D();

            // Convert linear arrays back to dictionary format
            var dict = new Dictionary<int, (double X, double Y)>(count);
            for (int i = 0; i < count; i++)
                dict[topology.ElementIds[i]] = (gradXHost[i], gradYHost[i]);

            return new VectorField(dict);
        }

        /// <summary>
        /// CPU implementation of 5-point Laplacian operator on D2Q9 mesh.
        /// Computes ∇²φ = ∂²φ/∂x² + ∂²φ/∂y² using second-order finite differences.
        /// Uses standard 5-point stencil: Δφ = φ_R + φ_L + φ_U + φ_D - 4φ_0
        /// </summary>
        /// <param name="mesh">LBM grid with D2Q9 connectivity</param>
        /// <param name="φ">Scalar field to compute Laplacian of</param>
        /// <returns>Scalar field containing Laplacian values</returns>
        public static ScalarField CalculateLaplacian(LBMGrid mesh, ScalarField φ)
        {
            var lap = new Dictionary<int, double>();
            
            // Process each element in the mesh
            foreach (LBMElement el in mesh.GetElements().Cast<LBMElement>())
            {
                // Get neighbors in cardinal directions
                var R = el.Neighbors[1];    // Right neighbor
                var U = el.Neighbors[2];    // Up neighbor
                var L = el.Neighbors[3];    // Left neighbor
                var D = el.Neighbors[4];    // Down neighbor

                // Get field values (use center value for missing/wall neighbors)
                double φ0 = φ.GetValue(el.Id);
                double φr = R != null && !R.IsWall ? φ.GetValue(R.Id) : φ0;
                double φl = L != null && !L.IsWall ? φ.GetValue(L.Id) : φ0;
                double φu = U != null && !U.IsWall ? φ.GetValue(U.Id) : φ0;
                double φd = D != null && !D.IsWall ? φ.GetValue(D.Id) : φ0;

                // Compute 5-point Laplacian: sum of neighbors minus 4 times center
                lap[el.Id] = φr + φl + φu + φd - 4 * φ0;
            }

            return new PotentialDistribution(lap);
        }

        /// <summary>
        /// GPU-accelerated Laplacian computation using CUDA kernels.
        /// Parallelizes Laplacian calculation across all mesh elements.
        /// </summary>
        /// <param name="mesh">LBM grid with flattened topology</param>
        /// <param name="φ">Scalar field to compute Laplacian of</param>
        /// <returns>Scalar field containing Laplacian values</returns>
        public static ScalarField CalculateLaplacianCuda(LBMGrid mesh, ScalarField φ)
        {
            // Convert to GPU topology and handle empty case
            var topology = LatticeBoltzmannCudaHelper.BuildTopology(mesh);
            int count = topology.ElementCount;
            if (count == 0)
                return new PotentialDistribution(new Dictionary<int, double>());

            EnsureCudaKernels();

            // Prepare field data for GPU
            var fieldValues = new double[count];
            for (int i = 0; i < count; i++)
                fieldValues[i] = φ.GetValue(topology.ElementIds[i]);

            var accelerator = LatticeBoltzmannCudaContext.Accelerator;

            // Allocate GPU memory
            using var fieldBuffer = accelerator.Allocate1D<double>(count);
            using var laplacianBuffer = accelerator.Allocate1D<double>(count);
            using var neighborIndexBuffer = accelerator.Allocate1D<int>(count * 9);
            using var neighborIsWallBuffer = accelerator.Allocate1D<int>(count * 9);

            // Transfer data to GPU
            fieldBuffer.CopyFromCPU(fieldValues);
            neighborIndexBuffer.CopyFromCPU(topology.NeighborIndices);
            neighborIsWallBuffer.CopyFromCPU(topology.NeighborIsWall);

            // Execute Laplacian kernel
            if (_laplacianKernel == null)
                throw new NullReferenceException();

            _laplacianKernel(count,
                fieldBuffer.View,
                neighborIndexBuffer.View,
                neighborIsWallBuffer.View,
                laplacianBuffer.View);

            // Get results from GPU
            accelerator.Synchronize();
            var lapHost = laplacianBuffer.GetAsArray1D();
            
            // Convert back to dictionary format
            var dict = new Dictionary<int, double>(count);
            for (int i = 0; i < count; i++)
                dict[topology.ElementIds[i]] = lapHost[i];

            return new PotentialDistribution(dict);
        }

        /// <summary>
        /// CPU implementation of divergence operator on D2Q9 mesh.
        /// Computes ∇·F = ∂Fx/∂x + ∂Fy/∂y using central differences.
        /// Used for analyzing current flow and checking conservation laws.
        /// </summary>
        /// <param name="mesh">LBM grid with D2Q9 connectivity</param>
        /// <param name="F">Vector field to compute divergence of</param>
        /// <returns>Scalar field containing divergence values</returns>
        public static ScalarField CalculateDivergence(LBMGrid mesh, VectorField F)
        {
            var div = new Dictionary<int, double>();
            
            foreach (LBMElement el in mesh.GetElements().Cast<LBMElement>())
            {
                // Get neighbors in cardinal directions
                var R = el.Neighbors[1];
                var U = el.Neighbors[2];
                var L = el.Neighbors[3];
                var D = el.Neighbors[4];

                // Get vector field components at all points
                var (Fx0, Fy0) = F.GetVector(el.Id);  // Center values
                
                // Get neighbor values (fallback to center if unavailable)
                var (Fxr, Fyr) = R != null ? F.GetVector(R.Id) : (Fx0, Fy0);
                var (Fxl, Fyl) = L != null ? F.GetVector(L.Id) : (Fx0, Fy0);
                var (Fxu, Fyu) = U != null ? F.GetVector(U.Id) : (Fx0, Fy0);
                var (Fxd, Fyd) = D != null ? F.GetVector(D.Id) : (Fx0, Fy0);

                // Compute partial derivatives using central differences
                double dFx = (Fxr - Fxl) / (R != null && L != null ? 2.0 : 1.0);  // ∂Fx/∂x
                double dFy = (Fyu - Fyd) / (U != null && D != null ? 2.0 : 1.0);  // ∂Fy/∂y

                // Divergence is sum of partial derivatives
                div[el.Id] = dFx + dFy;
            }

            return new PotentialDistribution(div);
        }

        /// <summary>
        /// GPU-accelerated divergence computation using CUDA kernels.
        /// Processes vector field components in parallel across all elements.
        /// </summary>
        /// <param name="mesh">LBM grid with flattened topology</param>
        /// <param name="F">Vector field to compute divergence of</param>
        /// <returns>Scalar field containing divergence values</returns>
        public static ScalarField CalculateDivergenceCuda(LBMGrid mesh, VectorField F)
        {
            var topology = LatticeBoltzmannCudaHelper.BuildTopology(mesh);
            int count = topology.ElementCount;
            if (count == 0)
                return new PotentialDistribution(new Dictionary<int, double>());

            EnsureCudaKernels();

            // Prepare vector field components for GPU
            var fxHost = new double[count];
            var fyHost = new double[count];
            for (int i = 0; i < count; i++)
            {
                var (fx, fy) = F.GetVector(topology.ElementIds[i]);
                fxHost[i] = fx;
                fyHost[i] = fy;
            }

            var accelerator = LatticeBoltzmannCudaContext.Accelerator;

            // Allocate GPU memory for vector field and results
            using var fxBuffer = accelerator.Allocate1D<double>(count);
            using var fyBuffer = accelerator.Allocate1D<double>(count);
            using var neighborIndexBuffer = accelerator.Allocate1D<int>(count * 9);
            using var neighborIsWallBuffer = accelerator.Allocate1D<int>(count * 9);
            using var resultBuffer = accelerator.Allocate1D<double>(count);

            // Transfer data to GPU
            fxBuffer.CopyFromCPU(fxHost);
            fyBuffer.CopyFromCPU(fyHost);
            neighborIndexBuffer.CopyFromCPU(topology.NeighborIndices);
            neighborIsWallBuffer.CopyFromCPU(topology.NeighborIsWall);

            // Execute divergence kernel
            if(_divergenceKernel == null)
                throw new NullReferenceException(); 

            _divergenceKernel(count,
                fxBuffer.View,                  // X-component of vector field
                fyBuffer.View,                  // Y-component of vector field
                neighborIndexBuffer.View,       // Neighbor connectivity
                neighborIsWallBuffer.View,      // Neighbor wall flags
                resultBuffer.View);             // Output divergence field

            // Get results and convert to dictionary
            accelerator.Synchronize();
            var divHost = resultBuffer.GetAsArray1D();
            var dict = new Dictionary<int, double>(count);
            for (int i = 0; i < count; i++)
                dict[topology.ElementIds[i]] = divHost[i];

            return new PotentialDistribution(dict);
        }

        /// <summary>
        /// Thread-safe compilation of CUDA kernels for differential operators.
        /// Uses lazy initialization to compile kernels only when needed.
        /// </summary>
        private static void EnsureCudaKernels()
        {
            LatticeBoltzmannCudaContext.EnsureInitialized();
            if (_gradientKernel != null)
                return;

            lock (_cudaKernelLock)
            {
                if (_gradientKernel != null)
                    return;

                // Compile all operator kernels with automatic optimization
                var accelerator = LatticeBoltzmannCudaContext.Accelerator;
                _gradientKernel = accelerator.LoadAutoGroupedStreamKernel<Index1D, ArrayView<double>, ArrayView<int>, ArrayView<int>, ArrayView<double>, ArrayView<double>>(GradientKernel);
                _laplacianKernel = accelerator.LoadAutoGroupedStreamKernel<Index1D, ArrayView<double>, ArrayView<int>, ArrayView<int>, ArrayView<double>>(LaplacianKernel);
                _divergenceKernel = accelerator.LoadAutoGroupedStreamKernel<Index1D, ArrayView<double>, ArrayView<double>, ArrayView<int>, ArrayView<int>, ArrayView<double>>(DivergenceKernel);
            }
        }

        /// <summary>
        /// CUDA kernel for computing gradient of scalar field using central differences.
        /// Each thread processes one element and computes both X and Y derivatives.
        /// </summary>
        /// <param name="index">Element index (thread ID)</param>
        /// <param name="field">Input scalar field values</param>
        /// <param name="neighborIndices">Neighbor connectivity array</param>
        /// <param name="neighborIsWall">Neighbor wall flags</param>
        /// <param name="gradX">Output X-gradient</param>
        /// <param name="gradY">Output Y-gradient</param>
        private static void GradientKernel(
            Index1D index,
            ArrayView<double> field,
            ArrayView<int> neighborIndices,
            ArrayView<int> neighborIsWall,
            ArrayView<double> gradX,
            ArrayView<double> gradY)
        {
            // Calculate base index for this element's neighbors
            int baseIndex = index * 9;
            double center = field[index];

            // Get neighbor values in cardinal directions (indices 1-4 in D2Q9)
            // Right neighbor (direction 1)
            int rightIndex = neighborIndices[baseIndex + 1];
            double rightValue = center;  // Default to center value
            if (rightIndex >= 0 && neighborIsWall[baseIndex + 1] == 0)
                rightValue = field[rightIndex];

            // Left neighbor (direction 3)
            int leftIndex = neighborIndices[baseIndex + 3];
            double leftValue = center;
            if (leftIndex >= 0 && neighborIsWall[baseIndex + 3] == 0)
                leftValue = field[leftIndex];

            // Up neighbor (direction 2)
            int upIndex = neighborIndices[baseIndex + 2];
            double upValue = center;
            if (upIndex >= 0 && neighborIsWall[baseIndex + 2] == 0)
                upValue = field[upIndex];

            // Down neighbor (direction 4)
            int downIndex = neighborIndices[baseIndex + 4];
            double downValue = center;
            if (downIndex >= 0 && neighborIsWall[baseIndex + 4] == 0)
                downValue = field[downIndex];

            // Compute denominators for finite difference (central vs one-sided)
            double denomX = rightIndex >= 0 && leftIndex >= 0 ? 2.0 : 1.0;
            double denomY = upIndex >= 0 && downIndex >= 0 ? 2.0 : 1.0;

            // Calculate partial derivatives
            gradX[index] = (rightValue - leftValue) / denomX;  // ∂φ/∂x
            gradY[index] = (upValue - downValue) / denomY;     // ∂φ/∂y
        }

        /// <summary>
        /// CUDA kernel for computing Laplacian using 5-point stencil.
        /// Each thread processes one element to compute ∇²φ.
        /// </summary>
        /// <param name="index">Element index (thread ID)</param>
        /// <param name="field">Input scalar field values</param>
        /// <param name="neighborIndices">Neighbor connectivity array</param>
        /// <param name="neighborIsWall">Neighbor wall flags</param>
        /// <param name="result">Output Laplacian values</param>
        private static void LaplacianKernel(
            Index1D index,
            ArrayView<double> field,
            ArrayView<int> neighborIndices,
            ArrayView<int> neighborIsWall,
            ArrayView<double> result)
        {
            int baseIndex = index * 9;
            double center = field[index];

            // Get values at cardinal neighbors (same pattern as gradient)
            int rightIndex = neighborIndices[baseIndex + 1];
            double rightValue = center;
            if (rightIndex >= 0 && neighborIsWall[baseIndex + 1] == 0)
                rightValue = field[rightIndex];

            int leftIndex = neighborIndices[baseIndex + 3];
            double leftValue = center;
            if (leftIndex >= 0 && neighborIsWall[baseIndex + 3] == 0)
                leftValue = field[leftIndex];

            int upIndex = neighborIndices[baseIndex + 2];
            double upValue = center;
            if (upIndex >= 0 && neighborIsWall[baseIndex + 2] == 0)
                upValue = field[upIndex];

            int downIndex = neighborIndices[baseIndex + 4];
            double downValue = center;
            if (downIndex >= 0 && neighborIsWall[baseIndex + 4] == 0)
                downValue = field[downIndex];

            // 5-point Laplacian stencil: sum of neighbors minus 4 times center
            result[index] = rightValue + leftValue + upValue + downValue - 4.0 * center;
        }

        /// <summary>
        /// CUDA kernel for computing divergence of vector field.
        /// Each thread processes one element to compute ∇·F = ∂Fx/∂x + ∂Fy/∂y.
        /// </summary>
        /// <param name="index">Element index (thread ID)</param>
        /// <param name="fx">X-component of vector field</param>
        /// <param name="fy">Y-component of vector field</param>
        /// <param name="neighborIndices">Neighbor connectivity array</param>
        /// <param name="neighborIsWall">Neighbor wall flags</param>
        /// <param name="result">Output divergence values</param>
        private static void DivergenceKernel(
            Index1D index,
            ArrayView<double> fx,
            ArrayView<double> fy,
            ArrayView<int> neighborIndices,
            ArrayView<int> neighborIsWall,
            ArrayView<double> result)
        {
            int baseIndex = index * 9;
            double fx0 = fx[index];  // Center X-component
            double fy0 = fy[index];  // Center Y-component

            // Get X-component values at neighbors for ∂Fx/∂x
            int rightIndex = neighborIndices[baseIndex + 1];
            double fxr = fx0;
            if (rightIndex >= 0 && neighborIsWall[baseIndex + 1] == 0)
                fxr = fx[rightIndex];

            int leftIndex = neighborIndices[baseIndex + 3];
            double fxl = fx0;
            if (leftIndex >= 0 && neighborIsWall[baseIndex + 3] == 0)
                fxl = fx[leftIndex];

            // Get Y-component values at neighbors for ∂Fy/∂y
            int upIndex = neighborIndices[baseIndex + 2];
            double fyu = fy0;
            if (upIndex >= 0 && neighborIsWall[baseIndex + 2] == 0)
                fyu = fy[upIndex];

            int downIndex = neighborIndices[baseIndex + 4];
            double fyd = fy0;
            if (downIndex >= 0 && neighborIsWall[baseIndex + 4] == 0)
                fyd = fy[downIndex];

            // Compute finite difference denominators
            double denomX = rightIndex >= 0 && leftIndex >= 0 ? 2.0 : 1.0;
            double denomY = upIndex >= 0 && downIndex >= 0 ? 2.0 : 1.0;

            // Divergence = ∂Fx/∂x + ∂Fy/∂y
            result[index] = (fxr - fxl) / denomX + (fyu - fyd) / denomY;
        }
    }
}
