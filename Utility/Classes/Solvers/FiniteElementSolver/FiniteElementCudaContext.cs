using ILGPU;
using ILGPU.Runtime;
using ILGPU.Runtime.Cuda;

using Accelerator = ILGPU.Runtime.Accelerator;

namespace Utility.Classes.Solvers.FiniteElementSolver
{
    internal static class FiniteElementCudaContext
    {
        private static readonly object SyncRoot = new();

        private static Context? _context;
        private static Accelerator? _accelerator;
        private static bool _initializationAttempted;
        private static Action<Index1D, ArrayView<int>, ArrayView<int>, ArrayView<double>, ArrayView<double>, ArrayView<double>>? _assembleKernel;

        internal static bool? AvailabilityOverride { get; set; }

        internal static bool IsAvailable
        {
            get
            {
                if (AvailabilityOverride.HasValue)
                    return AvailabilityOverride.Value;

                EnsureInitialized();
                return _accelerator != null && _assembleKernel != null;
            }
        }

        internal static bool TryAssembleStiffnessValues(
            int[] entryElementIndices,
            int[] entryContributionSlots,
            double[] entryBaseValues,
            double[] conductivities,
            int contributionCount,
            out double[] values)
        {
            values = new double[contributionCount];

            if (AvailabilityOverride is false)
                return false;

            if (entryElementIndices.Length == 0 || contributionCount == 0)
                return true;

            EnsureInitialized();
            if (_accelerator == null || _assembleKernel == null)
                return false;

            using var entryElementBuffer = _accelerator.Allocate1D<int>(entryElementIndices.Length);
            using var entryContributionSlotBuffer = _accelerator.Allocate1D<int>(entryContributionSlots.Length);
            using var entryBaseValueBuffer = _accelerator.Allocate1D<double>(entryBaseValues.Length);
            using var conductivityBuffer = _accelerator.Allocate1D<double>(conductivities.Length);
            using var valueBuffer = _accelerator.Allocate1D<double>(contributionCount);

            entryElementBuffer.CopyFromCPU(entryElementIndices);
            entryContributionSlotBuffer.CopyFromCPU(entryContributionSlots);
            entryBaseValueBuffer.CopyFromCPU(entryBaseValues);
            conductivityBuffer.CopyFromCPU(conductivities);
            valueBuffer.View.MemSetToZero();

            _assembleKernel(
                entryBaseValues.Length,
                entryElementBuffer.View,
                entryContributionSlotBuffer.View,
                entryBaseValueBuffer.View,
                conductivityBuffer.View,
                valueBuffer.View);

            _accelerator.Synchronize();
            values = valueBuffer.GetAsArray1D();
            return true;
        }

        private static void EnsureInitialized()
        {
            if (_initializationAttempted)
                return;

            lock (SyncRoot)
            {
                if (_initializationAttempted)
                    return;

                _initializationAttempted = true;

                try
                {
                    _context = Context.Create(builder => builder.Cuda().Math(MathMode.Default));
                    var device = _context.GetPreferredDevice(preferCPU: false);
                    if (device.AcceleratorType != AcceleratorType.Cuda)
                        return;

                    _accelerator = device.CreateAccelerator(_context);
                    _assembleKernel = _accelerator.LoadAutoGroupedStreamKernel<
                        Index1D,
                        ArrayView<int>,
                        ArrayView<int>,
                        ArrayView<double>,
                        ArrayView<double>,
                        ArrayView<double>>(AssembleStiffnessKernel);
                }
                catch
                {
                    _accelerator?.Dispose();
                    _context?.Dispose();
                    _accelerator = null;
                    _context = null;
                    _assembleKernel = null;
                }
            }
        }

        private static void AssembleStiffnessKernel(
            Index1D index,
            ArrayView<int> entryElementIndices,
            ArrayView<int> entryContributionSlots,
            ArrayView<double> entryBaseValues,
            ArrayView<double> conductivities,
            ArrayView<double> assembledValues)
        {
            int elementIndex = entryElementIndices[index];
            double conductivity = conductivities[elementIndex];
            if (conductivity == 0.0)
                return;

            int contributionSlot = entryContributionSlots[index];
            double contribution = conductivity * entryBaseValues[index];
            Atomic.Add(ref assembledValues[contributionSlot], contribution);
        }
    }
}
