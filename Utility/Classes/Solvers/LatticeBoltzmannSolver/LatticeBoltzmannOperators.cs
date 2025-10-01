using ILGPU;
using ILGPU.Runtime;
using Utility.Classes.Discretizer.LatticeBoltzmannGrid;

namespace Utility.Classes.Solvers.LatticeBoltzmannSolver
{
    /// <summary>
    /// Contains helper methods for applying discrete differential operators to fields
    /// defined on a structured Lattice-Boltzmann grid.
    /// </summary>
    public static class LatticeBoltzmannOperators
    {
        private static readonly object _cudaKernelLock = new();
        private static Action<Index1D, ArrayView<double>, ArrayView<int>, ArrayView<int>, ArrayView<double>, ArrayView<double>>? _gradientKernel;
        private static Action<Index1D, ArrayView<double>, ArrayView<int>, ArrayView<int>, ArrayView<double>>? _laplacianKernel;
        private static Action<Index1D, ArrayView<double>, ArrayView<double>, ArrayView<int>, ArrayView<int>, ArrayView<double>>? _divergenceKernel;

        /// <summary>
        /// Central‐difference gradient of a scalar field φ on D2Q9 mesh (CPU implementation).
        /// </summary>
        public static VectorField CalculateGradient(LBMGrid mesh, ScalarField φ)
        {
            var grad = new Dictionary<int, (double X, double Y)>();
            foreach (LBMElement el in mesh.GetElements().Cast<LBMElement>())
            {
                var R = el.Neighbors[1];
                var U = el.Neighbors[2];
                var L = el.Neighbors[3];
                var D = el.Neighbors[4];

                double φ0 = φ.GetValue(el.Id);
                double φr = (R != null && !R.IsWall) ? φ.GetValue(R.Id) : φ0;
                double φl = (L != null && !L.IsWall) ? φ.GetValue(L.Id) : φ0;
                double φu = (U != null && !U.IsWall) ? φ.GetValue(U.Id) : φ0;
                double φd = (D != null && !D.IsWall) ? φ.GetValue(D.Id) : φ0;

                double dx = (φr - φl) / ((R != null && L != null) ? 2.0 : 1.0);
                double dy = (φu - φd) / ((U != null && D != null) ? 2.0 : 1.0);

                grad[el.Id] = (dx, dy);
            }

            return new VectorField(grad);
        }

        public static VectorField CalculateGradientCuda(LBMGrid mesh, ScalarField φ)
        {
            var topology = LatticeBoltzmannCudaHelper.BuildTopology(mesh);
            int count = topology.ElementCount;
            if (count == 0)
                return new VectorField(new Dictionary<int, (double, double)>());

            EnsureCudaKernels();

            var fieldValues = new double[count];
            for (int i = 0; i < count; i++)
                fieldValues[i] = φ.GetValue(topology.ElementIds[i]);

            var accelerator = LatticeBoltzmannCudaContext.Accelerator;

            using var fieldBuffer = accelerator.Allocate1D<double>(count);
            using var gradXBuffer = accelerator.Allocate1D<double>(count);
            using var gradYBuffer = accelerator.Allocate1D<double>(count);
            using var neighborIndexBuffer = accelerator.Allocate1D<int>(count * 9);
            using var neighborIsWallBuffer = accelerator.Allocate1D<int>(count * 9);

            fieldBuffer.CopyFromCPU(fieldValues);
            neighborIndexBuffer.CopyFromCPU(topology.NeighborIndices);
            neighborIsWallBuffer.CopyFromCPU(topology.NeighborIsWall);

            if (_gradientKernel == null)
                throw new NullReferenceException();

            _gradientKernel(count,
                fieldBuffer.View,
                neighborIndexBuffer.View,
                neighborIsWallBuffer.View,
                gradXBuffer.View,
                gradYBuffer.View);

            accelerator.Synchronize();
            var gradXHost = gradXBuffer.GetAsArray1D();
            var gradYHost = gradYBuffer.GetAsArray1D();

            var dict = new Dictionary<int, (double X, double Y)>(count);
            for (int i = 0; i < count; i++)
                dict[topology.ElementIds[i]] = (gradXHost[i], gradYHost[i]);

            return new VectorField(dict);
        }

        /// <summary>
        /// Standard 5-point Laplacian Δφ = φ_r+φ_l+φ_u+φ_d - 4φ0 (CPU implementation).
        /// </summary>
        public static ScalarField CalculateLaplacian(LBMGrid mesh, ScalarField φ)
        {
            var lap = new Dictionary<int, double>();
            foreach (LBMElement el in mesh.GetElements().Cast<LBMElement>())
            {
                var R = el.Neighbors[1];
                var U = el.Neighbors[2];
                var L = el.Neighbors[3];
                var D = el.Neighbors[4];

                double φ0 = φ.GetValue(el.Id);
                double φr = (R != null && !R.IsWall) ? φ.GetValue(R.Id) : φ0;
                double φl = (L != null && !L.IsWall) ? φ.GetValue(L.Id) : φ0;
                double φu = (U != null && !U.IsWall) ? φ.GetValue(U.Id) : φ0;
                double φd = (D != null && !D.IsWall) ? φ.GetValue(D.Id) : φ0;

                lap[el.Id] = φr + φl + φu + φd - 4 * φ0;
            }

            return new PotentialDistribution(lap);
        }

        public static ScalarField CalculateLaplacianCuda(LBMGrid mesh, ScalarField φ)
        {
            var topology = LatticeBoltzmannCudaHelper.BuildTopology(mesh);
            int count = topology.ElementCount;
            if (count == 0)
                return new PotentialDistribution(new Dictionary<int, double>());

            EnsureCudaKernels();

            var fieldValues = new double[count];
            for (int i = 0; i < count; i++)
                fieldValues[i] = φ.GetValue(topology.ElementIds[i]);

            var accelerator = LatticeBoltzmannCudaContext.Accelerator;

            using var fieldBuffer = accelerator.Allocate1D<double>(count);
            using var laplacianBuffer = accelerator.Allocate1D<double>(count);
            using var neighborIndexBuffer = accelerator.Allocate1D<int>(count * 9);
            using var neighborIsWallBuffer = accelerator.Allocate1D<int>(count * 9);

            fieldBuffer.CopyFromCPU(fieldValues);
            neighborIndexBuffer.CopyFromCPU(topology.NeighborIndices);
            neighborIsWallBuffer.CopyFromCPU(topology.NeighborIsWall);

            if (_laplacianKernel == null)
                throw new NullReferenceException();

            _laplacianKernel(count,
                fieldBuffer.View,
                neighborIndexBuffer.View,
                neighborIsWallBuffer.View,
                laplacianBuffer.View);

            accelerator.Synchronize();
            var lapHost = laplacianBuffer.GetAsArray1D();
            var dict = new Dictionary<int, double>(count);
            for (int i = 0; i < count; i++)
                dict[topology.ElementIds[i]] = lapHost[i];

            return new PotentialDistribution(dict);
        }

        /// <summary>
        /// Divergence ∇·F using neighbor-based differences (CPU implementation).
        /// </summary>
        public static ScalarField CalculateDivergence(LBMGrid mesh, VectorField F)
        {
            var div = new Dictionary<int, double>();
            foreach (LBMElement el in mesh.GetElements().Cast<LBMElement>())
            {
                var R = el.Neighbors[1];
                var U = el.Neighbors[2];
                var L = el.Neighbors[3];
                var D = el.Neighbors[4];

                var (Fx0, Fy0) = F.GetVector(el.Id);
                var (Fxr, Fyr) = (R != null) ? F.GetVector(R.Id) : (Fx0, Fy0);
                var (Fxl, Fyl) = (L != null) ? F.GetVector(L.Id) : (Fx0, Fy0);
                var (Fxu, Fyu) = (U != null) ? F.GetVector(U.Id) : (Fx0, Fy0);
                var (Fxd, Fyd) = (D != null) ? F.GetVector(D.Id) : (Fx0, Fy0);

                double dFx = (Fxr - Fxl) / ((R != null && L != null) ? 2.0 : 1.0);
                double dFy = (Fyu - Fyd) / ((U != null && D != null) ? 2.0 : 1.0);

                div[el.Id] = dFx + dFy;
            }

            return new PotentialDistribution(div);
        }

        public static ScalarField CalculateDivergenceCuda(LBMGrid mesh, VectorField F)
        {
            var topology = LatticeBoltzmannCudaHelper.BuildTopology(mesh);
            int count = topology.ElementCount;
            if (count == 0)
                return new PotentialDistribution(new Dictionary<int, double>());

            EnsureCudaKernels();

            var fxHost = new double[count];
            var fyHost = new double[count];
            for (int i = 0; i < count; i++)
            {
                var (fx, fy) = F.GetVector(topology.ElementIds[i]);
                fxHost[i] = fx;
                fyHost[i] = fy;
            }

            var accelerator = LatticeBoltzmannCudaContext.Accelerator;

            using var fxBuffer = accelerator.Allocate1D<double>(count);
            using var fyBuffer = accelerator.Allocate1D<double>(count);
            using var neighborIndexBuffer = accelerator.Allocate1D<int>(count * 9);
            using var neighborIsWallBuffer = accelerator.Allocate1D<int>(count * 9);
            using var resultBuffer = accelerator.Allocate1D<double>(count);

            fxBuffer.CopyFromCPU(fxHost);
            fyBuffer.CopyFromCPU(fyHost);
            neighborIndexBuffer.CopyFromCPU(topology.NeighborIndices);
            neighborIsWallBuffer.CopyFromCPU(topology.NeighborIsWall);

            if(_divergenceKernel == null)
                throw new NullReferenceException(); 

            _divergenceKernel(count,
                fxBuffer.View,
                fyBuffer.View,
                neighborIndexBuffer.View,
                neighborIsWallBuffer.View,
                resultBuffer.View);

            accelerator.Synchronize();
            var divHost = resultBuffer.GetAsArray1D();
            var dict = new Dictionary<int, double>(count);
            for (int i = 0; i < count; i++)
                dict[topology.ElementIds[i]] = divHost[i];

            return new PotentialDistribution(dict);
        }

        private static void EnsureCudaKernels()
        {
            LatticeBoltzmannCudaContext.EnsureInitialized();
            if (_gradientKernel != null)
                return;

            lock (_cudaKernelLock)
            {
                if (_gradientKernel != null)
                    return;

                var accelerator = LatticeBoltzmannCudaContext.Accelerator;
                _gradientKernel = accelerator.LoadAutoGroupedStreamKernel<Index1D, ArrayView<double>, ArrayView<int>, ArrayView<int>, ArrayView<double>, ArrayView<double>>(GradientKernel);
                _laplacianKernel = accelerator.LoadAutoGroupedStreamKernel<Index1D, ArrayView<double>, ArrayView<int>, ArrayView<int>, ArrayView<double>>(LaplacianKernel);
                _divergenceKernel = accelerator.LoadAutoGroupedStreamKernel<Index1D, ArrayView<double>, ArrayView<double>, ArrayView<int>, ArrayView<int>, ArrayView<double>>(DivergenceKernel);
            }
        }

        private static void GradientKernel(
            Index1D index,
            ArrayView<double> field,
            ArrayView<int> neighborIndices,
            ArrayView<int> neighborIsWall,
            ArrayView<double> gradX,
            ArrayView<double> gradY)
        {
            int baseIndex = index * 9;
            double center = field[index];

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

            double denomX = (rightIndex >= 0 && leftIndex >= 0) ? 2.0 : 1.0;
            double denomY = (upIndex >= 0 && downIndex >= 0) ? 2.0 : 1.0;

            gradX[index] = (rightValue - leftValue) / denomX;
            gradY[index] = (upValue - downValue) / denomY;
        }

        private static void LaplacianKernel(
            Index1D index,
            ArrayView<double> field,
            ArrayView<int> neighborIndices,
            ArrayView<int> neighborIsWall,
            ArrayView<double> result)
        {
            int baseIndex = index * 9;
            double center = field[index];

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

            result[index] = rightValue + leftValue + upValue + downValue - 4.0 * center;
        }

        private static void DivergenceKernel(
            Index1D index,
            ArrayView<double> fx,
            ArrayView<double> fy,
            ArrayView<int> neighborIndices,
            ArrayView<int> neighborIsWall,
            ArrayView<double> result)
        {
            int baseIndex = index * 9;
            double fx0 = fx[index];
            double fy0 = fy[index];

            int rightIndex = neighborIndices[baseIndex + 1];
            double fxr = fx0;
            if (rightIndex >= 0 && neighborIsWall[baseIndex + 1] == 0)
                fxr = fx[rightIndex];

            int leftIndex = neighborIndices[baseIndex + 3];
            double fxl = fx0;
            if (leftIndex >= 0 && neighborIsWall[baseIndex + 3] == 0)
                fxl = fx[leftIndex];

            int upIndex = neighborIndices[baseIndex + 2];
            double fyu = fy0;
            if (upIndex >= 0 && neighborIsWall[baseIndex + 2] == 0)
                fyu = fy[upIndex];

            int downIndex = neighborIndices[baseIndex + 4];
            double fyd = fy0;
            if (downIndex >= 0 && neighborIsWall[baseIndex + 4] == 0)
                fyd = fy[downIndex];

            double denomX = (rightIndex >= 0 && leftIndex >= 0) ? 2.0 : 1.0;
            double denomY = (upIndex >= 0 && downIndex >= 0) ? 2.0 : 1.0;

            result[index] = (fxr - fxl) / denomX + (fyu - fyd) / denomY;
        }
    }
}
