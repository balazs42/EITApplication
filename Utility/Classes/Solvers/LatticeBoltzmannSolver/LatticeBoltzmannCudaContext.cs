using ILGPU;
using ILGPU.Runtime;
using ILGPU.Runtime.Cuda;

namespace Utility.Classes.Solvers.LatticeBoltzmannSolver
{
    /// <summary>
    /// Manages CUDA context initialization and provides GPU resources for LBM computations.
    /// This singleton class handles GPU memory allocation and kernel compilation for high-performance LBM solving.
    /// Uses ILGPU library to abstract CUDA programming while maintaining performance.
    /// </summary>
    internal static class LatticeBoltzmannCudaContext
    {
        /// <summary>
        /// Thread-safe lock object to ensure only one thread initializes CUDA context.
        /// Prevents race conditions during concurrent access to GPU resources.
        /// </summary>
        private static readonly object SyncRoot = new();
        
        /// <summary>
        /// ILGPU context managing GPU device and compilation pipeline.
        /// This is the root object for all GPU operations and kernel management.
        /// </summary>
        private static Context? _context;
        
        /// <summary>
        /// GPU accelerator instance representing the actual CUDA device.
        /// Provides access to GPU memory allocation and kernel execution capabilities.
        /// </summary>
        private static Accelerator? _accelerator;
        
        /// <summary>
        /// GPU memory buffer storing the equilibrium weights for all 9 D2Q9 directions.
        /// Pre-allocated on GPU to avoid repeated CPU-to-GPU transfers during computation.
        /// </summary>
        private static MemoryBuffer1D<double, Stride1D.Dense>? _weightsBuffer;
        
        /// <summary>
        /// GPU memory buffer storing opposite direction indices for bounce-back operations.
        /// Used by streaming kernel to implement wall boundary conditions efficiently.
        /// </summary>
        private static MemoryBuffer1D<int, Stride1D.Dense>? _oppositeBuffer;

        /// <summary>
        /// Thread-safe property providing access to the initialized GPU accelerator.
        /// Automatically initializes CUDA context on first access if not already done.
        /// </summary>
        public static Accelerator Accelerator
        {
            get
            {
                EnsureInitialized(); // Lazy initialization pattern
                return _accelerator!; // Guaranteed non-null after initialization
            }
        }

        /// <summary>
        /// Thread-safe property providing GPU memory view of equilibrium weights.
        /// Kernels use this to access pre-computed D2Q9 weights without CPU transfers.
        /// </summary>
        public static ArrayView1D<double, Stride1D.Dense> WeightsView
        {
            get
            {
                EnsureInitialized(); // Ensure GPU buffers are allocated
                return _weightsBuffer!.View; // Return memory view for kernel access
            }
        }

        /// <summary>
        /// Thread-safe property providing GPU memory view of opposite direction mapping.
        /// Used by streaming and boundary condition kernels for efficient wall handling.
        /// </summary>
        public static ArrayView1D<int, Stride1D.Dense> OppositeView
        {
            get
            {
                EnsureInitialized(); // Ensure GPU buffers are allocated
                return _oppositeBuffer!.View; // Return memory view for kernel access
            }
        }

        /// <summary>
        /// Initializes CUDA context, allocates GPU memory, and prepares constant data.
        /// This method is thread-safe and ensures initialization happens only once.
        /// Throws exception if no CUDA-capable GPU is available.
        /// </summary>
        public static void EnsureInitialized()
        {
            // Quick check without locking for performance
            if (_accelerator != null)
                return;

            // Thread-safe initialization using double-checked locking pattern
            lock (SyncRoot)
            {
                // Double-check after acquiring lock to prevent race conditions
                if (_accelerator != null)
                    return;

                // Create ILGPU context with CUDA backend and default math mode
                // Math mode controls floating-point precision and performance trade-offs
                _context = Context.Create(builder => builder.Cuda().Math(MathMode.Default));
                
                // Get the best available GPU device, preferring discrete GPUs over integrated
                var device = _context.GetPreferredDevice(preferCPU: false);
                
                // Verify we actually got a CUDA device, not CPU fallback
                if (device.AcceleratorType != AcceleratorType.Cuda)
                    throw new InvalidOperationException("No CUDA accelerator available for Lattice Boltzmann solver.");

                // Create accelerator instance from the CUDA device
                _accelerator = device.CreateAccelerator(_context);
                
                // Allocate and copy D2Q9 equilibrium weights to GPU memory
                // These weights are used in collision and initialization kernels
                _weightsBuffer = _accelerator.Allocate1D(LatticeBoltzmannConstants.Weights);
                
                // Allocate and copy opposite direction mapping to GPU memory
                // Used for efficient bounce-back boundary condition implementation
                _oppositeBuffer = _accelerator.Allocate1D(LatticeBoltzmannConstants.Opposite);
            }
        }
    }
}
