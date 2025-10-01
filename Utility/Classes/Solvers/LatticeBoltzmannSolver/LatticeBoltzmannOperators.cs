using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Threading.Tasks;
using ILGPU;
using ILGPU.Runtime;
using Utility.Classes.Measurement;
using Utility.Classes.Discretizer.LatticeBoltzmannGrid;

namespace Utility.Classes.Solvers.LatticeBoltzmannSolver
{
    /// <summary>
    /// Contains helper methods for applying discrete differential operators to fields
    /// defined on a structured Lattice-Boltzmann grid.
    /// This implementation is "element-centric", meaning it uses the neighbor-linking
    /// of the LBMElement class rather than coordinate-based lookups.
    /// </summary>
    public static class LatticeBoltzmannOperators
    {
        /// <summary>
        /// Central‐difference gradient of a scalar field φ on D2Q9 mesh.
        /// </summary>
        public static VectorField CalculateGradient(LBMGrid mesh, ScalarField φ)
            => CalculateGradientCpu(mesh, φ, useParallel: false);

        public static VectorField CalculateGradientCuda(LBMGrid mesh, ScalarField φ)
        {
            try
            {
                return CalculateGradientGpu(mesh, φ);
            }
            catch (Exception ex) when (ex is NotSupportedException or AcceleratorException)
            {
                return CalculateGradientCpu(mesh, φ, useParallel: true);
            }
        }

        private static VectorField CalculateGradientCpu(LBMGrid mesh, ScalarField φ, bool useParallel)
        {
            if (useParallel)
            {
                var concurrent = new ConcurrentDictionary<int, (double X, double Y)>();
                var elements = mesh.GetElements().Cast<LBMElement>().ToArray();
                Parallel.ForEach(elements, el =>
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

                    concurrent[el.Id] = (dx, dy);
                });

                return new VectorField(concurrent.ToDictionary(kvp => kvp.Key, kvp => kvp.Value));
            }

            var grad = new Dictionary<int, (double X, double Y)>();
            var seqElements = mesh.GetElements().Cast<LBMElement>();

            foreach (LBMElement el in seqElements)
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

        /// <summary>
        /// Standard 5-point Laplacian Δφ = φ_r+φ_l+φ_u+φ_d - 4φ0.
        /// </summary>
        public static ScalarField CalculateLaplacian(LBMGrid mesh, ScalarField φ)
            => CalculateLaplacianCpu(mesh, φ, useParallel: false);

        public static ScalarField CalculateLaplacianCuda(LBMGrid mesh, ScalarField φ)
        {
            try
            {
                return CalculateLaplacianGpu(mesh, φ);
            }
            catch (Exception ex) when (ex is NotSupportedException or AcceleratorException)
            {
                return CalculateLaplacianCpu(mesh, φ, useParallel: true);
            }
        }

        private static ScalarField CalculateLaplacianCpu(LBMGrid mesh, ScalarField φ, bool useParallel)
        {
            if (useParallel)
            {
                var concurrent = new ConcurrentDictionary<int, double>();
                var elements = mesh.GetElements().Cast<LBMElement>().ToArray();
                Parallel.ForEach(elements, el =>
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

                    concurrent[el.Id] = φr + φl + φu + φd - 4 * φ0;
                });

                return new PotentialDistribution(concurrent.ToDictionary(kvp => kvp.Key, kvp => kvp.Value));
            }

            var lap = new Dictionary<int, double>();
            var seqElements = mesh.GetElements().Cast<LBMElement>();

            foreach (LBMElement el in seqElements)
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

        /// <summary>
        /// ∇·F = ∂Fx/∂x + ∂Fy/∂y, using neighbor-based differences.
        /// </summary>
        public static ScalarField CalculateDivergence(LBMGrid mesh, VectorField F)
            => CalculateDivergenceCpu(mesh, F, useParallel: false);

        public static ScalarField CalculateDivergenceCuda(LBMGrid mesh, VectorField F)
        {
            try
            {
                return CalculateDivergenceGpu(mesh, F);
            }
            catch (Exception ex) when (ex is NotSupportedException or AcceleratorException)
            {
                return CalculateDivergenceCpu(mesh, F, useParallel: true);
            }
        }

        private static ScalarField CalculateDivergenceCpu(LBMGrid mesh, VectorField F, bool useParallel)
        {
            if (useParallel)
            {
                var concurrent = new ConcurrentDictionary<int, double>();
                var elements = mesh.GetElements().Cast<LBMElement>().ToArray();
                Parallel.ForEach(elements, el =>
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

                    concurrent[el.Id] = dFx + dFy;
                });

                return new PotentialDistribution(concurrent.ToDictionary(kvp => kvp.Key, kvp => kvp.Value));
            }

            var div = new Dictionary<int, double>();
            var seqElements = mesh.GetElements().Cast<LBMElement>();

            foreach (LBMElement el in seqElements)
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

        private static VectorField CalculateGradientGpu(LBMGrid mesh, ScalarField φ)
        {
            var topology = LatticeBoltzmannGpuHelper.BuildTopology(mesh);
            int count = topology.Elements.Length;
            if (!CudaAcceleratorProvider.TryCreate(out var context, out var accelerator) || context == null || accelerator == null)
                throw new NotSupportedException("CUDA accelerator is not available.");

            using (context)
            using (accelerator)
            {
                var phiValues = new double[count];
                for (int i = 0; i < count; i++)
                    phiValues[i] = φ.GetValue(topology.Elements[i].Id);

                using var phiBuffer = accelerator.Allocate1D(phiValues);
                using var neighborIndices = accelerator.Allocate1D(topology.NeighborIndices);
                using var neighborExists = accelerator.Allocate1D(topology.NeighborExists);
                using var neighborIsWall = accelerator.Allocate1D(topology.NeighborIsWall);
                using var gradXBuffer = accelerator.Allocate1D<double>(count);
                using var gradYBuffer = accelerator.Allocate1D<double>(count);

                var kernel = accelerator.LoadAutoGroupedStreamKernel<Index1D, ArrayView<double>, ArrayView<int>, ArrayView<byte>, ArrayView<byte>, ArrayView<double>, ArrayView<double>>(GradientKernel);
                kernel(count, phiBuffer.View, neighborIndices.View, neighborExists.View, neighborIsWall.View, gradXBuffer.View, gradYBuffer.View);
                accelerator.Synchronize();

                var gradX = gradXBuffer.GetAsArray1D();
                var gradY = gradYBuffer.GetAsArray1D();

                var result = new Dictionary<int, (double X, double Y)>(count);
                for (int i = 0; i < count; i++)
                    result[topology.Elements[i].Id] = (gradX[i], gradY[i]);

                return new VectorField(result);
            }
        }

        private static ScalarField CalculateLaplacianGpu(LBMGrid mesh, ScalarField φ)
        {
            var topology = LatticeBoltzmannGpuHelper.BuildTopology(mesh);
            int count = topology.Elements.Length;
            if (!CudaAcceleratorProvider.TryCreate(out var context, out var accelerator) || context == null || accelerator == null)
                throw new NotSupportedException("CUDA accelerator is not available.");

            using (context)
            using (accelerator)
            {
                var phiValues = new double[count];
                for (int i = 0; i < count; i++)
                    phiValues[i] = φ.GetValue(topology.Elements[i].Id);

                using var phiBuffer = accelerator.Allocate1D(phiValues);
                using var neighborIndices = accelerator.Allocate1D(topology.NeighborIndices);
                using var neighborExists = accelerator.Allocate1D(topology.NeighborExists);
                using var neighborIsWall = accelerator.Allocate1D(topology.NeighborIsWall);
                using var laplacianBuffer = accelerator.Allocate1D<double>(count);

                var kernel = accelerator.LoadAutoGroupedStreamKernel<Index1D, ArrayView<double>, ArrayView<int>, ArrayView<byte>, ArrayView<byte>, ArrayView<double>>(LaplacianKernel);
                kernel(count, phiBuffer.View, neighborIndices.View, neighborExists.View, neighborIsWall.View, laplacianBuffer.View);
                accelerator.Synchronize();

                var laplacian = laplacianBuffer.GetAsArray1D();
                var result = new Dictionary<int, double>(count);
                for (int i = 0; i < count; i++)
                    result[topology.Elements[i].Id] = laplacian[i];

                return new PotentialDistribution(result);
            }
        }

        private static ScalarField CalculateDivergenceGpu(LBMGrid mesh, VectorField F)
        {
            var topology = LatticeBoltzmannGpuHelper.BuildTopology(mesh);
            int count = topology.Elements.Length;
            if (!CudaAcceleratorProvider.TryCreate(out var context, out var accelerator) || context == null || accelerator == null)
                throw new NotSupportedException("CUDA accelerator is not available.");

            using (context)
            using (accelerator)
            {
                var fxValues = new double[count];
                var fyValues = new double[count];
                for (int i = 0; i < count; i++)
                {
                    var (x, y) = F.GetVector(topology.Elements[i].Id);
                    fxValues[i] = x;
                    fyValues[i] = y;
                }

                using var fxBuffer = accelerator.Allocate1D(fxValues);
                using var fyBuffer = accelerator.Allocate1D(fyValues);
                using var neighborIndices = accelerator.Allocate1D(topology.NeighborIndices);
                using var neighborExists = accelerator.Allocate1D(topology.NeighborExists);
                using var divergenceBuffer = accelerator.Allocate1D<double>(count);

                var kernel = accelerator.LoadAutoGroupedStreamKernel<Index1D, ArrayView<double>, ArrayView<double>, ArrayView<int>, ArrayView<byte>, ArrayView<double>>(DivergenceKernel);
                kernel(count, fxBuffer.View, fyBuffer.View, neighborIndices.View, neighborExists.View, divergenceBuffer.View);
                accelerator.Synchronize();

                var divergence = divergenceBuffer.GetAsArray1D();
                var result = new Dictionary<int, double>(count);
                for (int i = 0; i < count; i++)
                    result[topology.Elements[i].Id] = divergence[i];

                return new PotentialDistribution(result);
            }
        }

        private static void GradientKernel(
            Index1D index,
            ArrayView<double> phi,
            ArrayView<int> neighborIndices,
            ArrayView<byte> neighborExists,
            ArrayView<byte> neighborIsWall,
            ArrayView<double> gradX,
            ArrayView<double> gradY)
        {
            if (index >= phi.Length)
                return;

            double φ0 = phi[index];
            int baseIdx = index * 9;

            int rIdx = neighborIndices[baseIdx + 1];
            int lIdx = neighborIndices[baseIdx + 3];
            int uIdx = neighborIndices[baseIdx + 2];
            int dIdx = neighborIndices[baseIdx + 4];

            double φr = (neighborExists[baseIdx + 1] == 1 && neighborIsWall[baseIdx + 1] == 0) ? phi[rIdx] : φ0;
            double φl = (neighborExists[baseIdx + 3] == 1 && neighborIsWall[baseIdx + 3] == 0) ? phi[lIdx] : φ0;
            double φu = (neighborExists[baseIdx + 2] == 1 && neighborIsWall[baseIdx + 2] == 0) ? phi[uIdx] : φ0;
            double φd = (neighborExists[baseIdx + 4] == 1 && neighborIsWall[baseIdx + 4] == 0) ? phi[dIdx] : φ0;

            double denomX = (neighborExists[baseIdx + 1] == 1 && neighborExists[baseIdx + 3] == 1) ? 2.0 : 1.0;
            double denomY = (neighborExists[baseIdx + 2] == 1 && neighborExists[baseIdx + 4] == 1) ? 2.0 : 1.0;

            gradX[index] = (φr - φl) / denomX;
            gradY[index] = (φu - φd) / denomY;
        }

        private static void LaplacianKernel(
            Index1D index,
            ArrayView<double> phi,
            ArrayView<int> neighborIndices,
            ArrayView<byte> neighborExists,
            ArrayView<byte> neighborIsWall,
            ArrayView<double> laplacian)
        {
            if (index >= phi.Length)
                return;

            double φ0 = phi[index];
            int baseIdx = index * 9;

            int rIdx = neighborIndices[baseIdx + 1];
            int lIdx = neighborIndices[baseIdx + 3];
            int uIdx = neighborIndices[baseIdx + 2];
            int dIdx = neighborIndices[baseIdx + 4];

            double φr = (neighborExists[baseIdx + 1] == 1 && neighborIsWall[baseIdx + 1] == 0) ? phi[rIdx] : φ0;
            double φl = (neighborExists[baseIdx + 3] == 1 && neighborIsWall[baseIdx + 3] == 0) ? phi[lIdx] : φ0;
            double φu = (neighborExists[baseIdx + 2] == 1 && neighborIsWall[baseIdx + 2] == 0) ? phi[uIdx] : φ0;
            double φd = (neighborExists[baseIdx + 4] == 1 && neighborIsWall[baseIdx + 4] == 0) ? phi[dIdx] : φ0;

            laplacian[index] = φr + φl + φu + φd - 4.0 * φ0;
        }

        private static void DivergenceKernel(
            Index1D index,
            ArrayView<double> fx,
            ArrayView<double> fy,
            ArrayView<int> neighborIndices,
            ArrayView<byte> neighborExists,
            ArrayView<double> divergence)
        {
            if (index >= fx.Length)
                return;

            int baseIdx = index * 9;
            double fx0 = fx[index];
            double fy0 = fy[index];

            int rIdx = neighborIndices[baseIdx + 1];
            int lIdx = neighborIndices[baseIdx + 3];
            int uIdx = neighborIndices[baseIdx + 2];
            int dIdx = neighborIndices[baseIdx + 4];

            double fxr = neighborExists[baseIdx + 1] == 1 ? fx[rIdx] : fx0;
            double fxl = neighborExists[baseIdx + 3] == 1 ? fx[lIdx] : fx0;
            double fyu = neighborExists[baseIdx + 2] == 1 ? fy[uIdx] : fy0;
            double fyd = neighborExists[baseIdx + 4] == 1 ? fy[dIdx] : fy0;

            double denomX = (neighborExists[baseIdx + 1] == 1 && neighborExists[baseIdx + 3] == 1) ? 2.0 : 1.0;
            double denomY = (neighborExists[baseIdx + 2] == 1 && neighborExists[baseIdx + 4] == 1) ? 2.0 : 1.0;

            double dFx = (fxr - fxl) / denomX;
            double dFy = (fyu - fyd) / denomY;

            divergence[index] = dFx + dFy;
        }

        /// <summary>
        /// Finite‐difference gradient of J wrt σ: dJ/dσ_i ≈ [J(σ+δ) - J(σ-δ)]/(2δ).
        /// </summary>
        public static ScalarField ComputeFiniteDifferenceGradient(
            LBMGrid mesh,
            LBMBoundaryCondition bc,
            Complex[] observed,
            LatticeBoltzmannSolver solver,
            double δ)
        {
            var baseσ = mesh.GetConductivityDistribution().Conductivities;
            var grad = new Dictionary<int, double>();

            // baseline
            var φ0 = solver.SolveForward(mesh, bc);
            var u0 = mesh.GetElectrodePotentials();
            double J0 = 0;
            for (int e = 0; e < observed.Length; e++)
                J0 += 0.5 * Math.Pow(u0[e] - observed[e].Real, 2);

            foreach (var kv in baseσ)
            {
                int id = kv.Key;
                double σi = kv.Value;

                // σ+δ
                mesh.SetConductivity(id, σi + δ);
                solver.SolveForward(mesh, bc);
                var uPlus = mesh.GetElectrodePotentials();
                double Jplus = 0;
                for (int e = 0; e < observed.Length; e++)
                    Jplus += 0.5 * Math.Pow(uPlus[e] - observed[e].Real, 2);

                // σ-δ
                mesh.SetConductivity(id, σi - δ);
                solver.SolveForward(mesh, bc);
                var uMinus = mesh.GetElectrodePotentials();
                double Jminus = 0;
                for (int e = 0; e < observed.Length; e++)
                    Jminus += 0.5 * Math.Pow(uMinus[e] - observed[e].Real, 2);

                // restore
                mesh.SetConductivity(id, σi);

                grad[id] = (Jplus - Jminus) / (2 * δ);
            }

            return new ConductivityDistribution(grad);
        }
    }
}
