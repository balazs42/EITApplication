using ILGPU;
using ILGPU.Runtime;
using ILGPU.Runtime.Cuda;

namespace Utility.Classes.Solvers.LatticeBoltzmannSolver
{
    internal static class LatticeBoltzmannCudaContext
    {
        private static readonly object SyncRoot = new();
        private static Context? _context;
        private static Accelerator? _accelerator;
        private static MemoryBuffer1D<double, Stride1D.Dense>? _weightsBuffer;
        private static MemoryBuffer1D<int, Stride1D.Dense>? _oppositeBuffer;

        public static Accelerator Accelerator
        {
            get
            {
                EnsureInitialized();
                return _accelerator!;
            }
        }

        public static ArrayView1D<double, Stride1D.Dense> WeightsView
        {
            get
            {
                EnsureInitialized();
                return _weightsBuffer!.View;
            }
        }

        public static ArrayView1D<int, Stride1D.Dense> OppositeView
        {
            get
            {
                EnsureInitialized();
                return _oppositeBuffer!.View;
            }
        }

        public static void EnsureInitialized()
        {
            if (_accelerator != null)
                return;

            lock (SyncRoot)
            {
                if (_accelerator != null)
                    return;

                _context = Context.Create(builder => builder.Cuda().Math(MathMode.DoublePrecision));
                var device = _context.GetPreferredDevice(preferCPU: false);
                if (device.AcceleratorType != AcceleratorType.Cuda)
                    throw new InvalidOperationException("No CUDA accelerator available for Lattice Boltzmann solver.");

                _accelerator = device.CreateAccelerator(_context);
                _weightsBuffer = _accelerator.Allocate1D(LatticeBoltzmannConstants.Weights);
                _oppositeBuffer = _accelerator.Allocate1D(LatticeBoltzmannConstants.Opposite);
            }
        }
    }
}
