using Utility.Classes.Discretizer.FiniteElementMesh;
using Utility.Classes.Reconstruction.NumericSolvers;

namespace Utility.Classes.Solvers.FiniteElementSolver
{
    public static class FiniteElementGpuExecutionPolicy
    {
        public static bool ShouldUseCudaForReconstruction(FEMMesh mesh)
        {
            ArgumentNullException.ThrowIfNull(mesh);

            int elementCount = mesh.ElementsTyped.Count;
            int systemDimension = mesh.Vertices.Count + Math.Max(0, mesh.GetElectrodes().Count - 1);

            return elementCount >= FiniteElementSolver.DefaultCudaAssemblyThresholdElements
                || systemDimension >= GmresSolver.DefaultCudaVectorThresholdDimension;
        }
    }
}
