using ILGPU;
using ILGPU.Runtime;
using ILGPU.Runtime.Cuda;

using Accelerator = ILGPU.Runtime.Accelerator;

namespace Utility.Classes.Reconstruction.NumericSolvers
{
    internal static class GmresCudaContext
    {
        private static readonly object SyncRoot = new();
        private const int MaxPartialSums = 1024;

        private static Context? _context;
        private static Accelerator? _accelerator;
        private static bool _initializationAttempted;
        private static Action<Index1D, ArrayView<double>, ArrayView<double>, ArrayView<double>>? _dotKernel;
        private static Action<Index1D, ArrayView<double>, ArrayView<double>, double>? _axpyKernel;
        private static Action<Index1D, ArrayView<double>, ArrayView<double>, double>? _scaleCopyKernel;

        internal static bool? AvailabilityOverride { get; set; }

        internal static bool IsAvailable
        {
            get
            {
                if (AvailabilityOverride.HasValue)
                    return AvailabilityOverride.Value;

                EnsureInitialized();
                return _accelerator != null && _dotKernel != null && _axpyKernel != null && _scaleCopyKernel != null;
            }
        }

        internal static bool TryCreateSession(int vectorLength, out GmresCudaSession? session)
        {
            session = null;

            if (AvailabilityOverride is false || vectorLength <= 0)
                return false;

            EnsureInitialized();
            if (_accelerator == null || _dotKernel == null || _axpyKernel == null || _scaleCopyKernel == null)
                return false;

            try
            {
                session = new GmresCudaSession(
                    _accelerator,
                    _dotKernel,
                    _axpyKernel,
                    _scaleCopyKernel,
                    vectorLength,
                    Math.Min(vectorLength, MaxPartialSums));
                return true;
            }
            catch
            {
                session?.Dispose();
                session = null;
                return false;
            }
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
                    _dotKernel = _accelerator.LoadAutoGroupedStreamKernel<
                        Index1D,
                        ArrayView<double>,
                        ArrayView<double>,
                        ArrayView<double>>(DotKernel);
                    _axpyKernel = _accelerator.LoadAutoGroupedStreamKernel<
                        Index1D,
                        ArrayView<double>,
                        ArrayView<double>,
                        double>(AxpyKernel);
                    _scaleCopyKernel = _accelerator.LoadAutoGroupedStreamKernel<
                        Index1D,
                        ArrayView<double>,
                        ArrayView<double>,
                        double>(ScaleCopyKernel);
                }
                catch
                {
                    _accelerator?.Dispose();
                    _context?.Dispose();
                    _accelerator = null;
                    _context = null;
                    _dotKernel = null;
                    _axpyKernel = null;
                    _scaleCopyKernel = null;
                }
            }
        }

        private static void DotKernel(
            Index1D index,
            ArrayView<double> left,
            ArrayView<double> right,
            ArrayView<double> partialSums)
        {
            double sum = 0.0;
            for (int i = (int)index; i < (int)left.Length; i += (int)partialSums.Length)
                sum += left[i] * right[i];

            partialSums[index] = sum;
        }

        private static void AxpyKernel(
            Index1D index,
            ArrayView<double> target,
            ArrayView<double> vector,
            double scale)
        {
            target[index] += scale * vector[index];
        }

        private static void ScaleCopyKernel(
            Index1D index,
            ArrayView<double> source,
            ArrayView<double> destination,
            double scale)
        {
            destination[index] = source[index] * scale;
        }
    }

    internal sealed class GmresCudaSession : IDisposable
    {
        private readonly Accelerator _accelerator;
        private readonly int _vectorLength;
        private readonly int _partialSumCount;
        private readonly Action<Index1D, ArrayView<double>, ArrayView<double>, ArrayView<double>> _dotKernel;
        private readonly Action<Index1D, ArrayView<double>, ArrayView<double>, double> _axpyKernel;
        private readonly Action<Index1D, ArrayView<double>, ArrayView<double>, double> _scaleCopyKernel;
        private readonly MemoryBuffer1D<double, Stride1D.Dense> _workingBuffer;
        private readonly MemoryBuffer1D<double, Stride1D.Dense> _operandBuffer;
        private readonly MemoryBuffer1D<double, Stride1D.Dense> _solutionBuffer;
        private readonly MemoryBuffer1D<double, Stride1D.Dense> _partialSumBuffer;

        public GmresCudaSession(
            Accelerator accelerator,
            Action<Index1D, ArrayView<double>, ArrayView<double>, ArrayView<double>> dotKernel,
            Action<Index1D, ArrayView<double>, ArrayView<double>, double> axpyKernel,
            Action<Index1D, ArrayView<double>, ArrayView<double>, double> scaleCopyKernel,
            int vectorLength,
            int partialSumCount)
        {
            _accelerator = accelerator;
            _vectorLength = vectorLength;
            _partialSumCount = partialSumCount;
            _dotKernel = dotKernel;
            _axpyKernel = axpyKernel;
            _scaleCopyKernel = scaleCopyKernel;
            _workingBuffer = accelerator.Allocate1D<double>(vectorLength);
            _operandBuffer = accelerator.Allocate1D<double>(vectorLength);
            _solutionBuffer = accelerator.Allocate1D<double>(vectorLength);
            _partialSumBuffer = accelerator.Allocate1D<double>(partialSumCount);
        }

        public void UploadWorkingVector(double[] values)
        {
            ArgumentNullException.ThrowIfNull(values);

            if (values.Length != _vectorLength)
                throw new ArgumentException("Vector length does not match the CUDA GMRES workspace.", nameof(values));

            _workingBuffer.CopyFromCPU(values);
        }

        public double DotAndSubtractFromWorking(double[] values)
        {
            UploadOperand(values);

            double dot = DotViews(_workingBuffer.View, _operandBuffer.View);
            if (dot == 0.0)
                return dot;

            _axpyKernel(_vectorLength, _workingBuffer.View, _operandBuffer.View, -dot);
            _accelerator.Synchronize();
            return dot;
        }

        public double ComputeWorkingNorm()
        {
            double squaredNorm = DotViews(_workingBuffer.View, _workingBuffer.View);
            return Math.Sqrt(Math.Max(0.0, squaredNorm));
        }

        public double[] ScaleWorkingToArray(double scale)
        {
            _scaleCopyKernel(_vectorLength, _workingBuffer.View, _operandBuffer.View, scale);
            _accelerator.Synchronize();
            return _operandBuffer.GetAsArray1D();
        }

        public void ResetSolution()
        {
            _solutionBuffer.View.MemSetToZero();
        }

        public void AxpySolution(double[] vector, double scale)
        {
            if (scale == 0.0)
                return;

            UploadOperand(vector);
            _axpyKernel(_vectorLength, _solutionBuffer.View, _operandBuffer.View, scale);
            _accelerator.Synchronize();
        }

        public double[] DownloadSolution() => _solutionBuffer.GetAsArray1D();

        public void Dispose()
        {
            _partialSumBuffer.Dispose();
            _solutionBuffer.Dispose();
            _operandBuffer.Dispose();
            _workingBuffer.Dispose();
        }

        private void UploadOperand(double[] values)
        {
            ArgumentNullException.ThrowIfNull(values);

            if (values.Length != _vectorLength)
                throw new ArgumentException("Vector length does not match the CUDA GMRES workspace.", nameof(values));

            _operandBuffer.CopyFromCPU(values);
        }

        private double DotViews(ArrayView<double> left, ArrayView<double> right)
        {
            _dotKernel(_partialSumCount, left, right, _partialSumBuffer.View);
            _accelerator.Synchronize();

            double sum = 0.0;
            var partials = _partialSumBuffer.GetAsArray1D();
            for (int i = 0; i < partials.Length; i++)
                sum += partials[i];

            return sum;
        }
    }
}
