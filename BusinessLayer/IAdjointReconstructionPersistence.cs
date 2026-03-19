using Utility.Classes;
using Utility.Classes.Measurement;
using Utility.Classes.Discretizer;
using Utility.Classes.Discretizer.FiniteElementMesh;
using Utility.Classes.Discretizer.LatticeBoltzmannGrid;
using Utility.Classes.ReconstructionParameters;
using Utility.Exports;

namespace BusinessLayer
{
    public interface IAdjointReconstructionPersistence : IReconstructionPersistence
    {
        void SetConductivityDistributions(ConductivityDistribution original, ConductivityDistribution initial);
        void InitializeReconstruction(IDiscretization discretization, ReconstructionRuntimeContext parameters, bool reinit);

        ReconstructionFrame Step(double[] measurement, BoundaryCondition boundaryCondition, double gradientStepSize, double redularizationStepSize);
        void Run(int maxIterationCount, double gradientStepSize, double redularizationStepSize);
        ReconstructionResult Stop();

        PotentialDistribution ForwardSolveStepFem();
        PotentialDistribution ForwardSolveStepLbm();
        PotentialDistribution ForwardSolveStepLbmCuda();

        ReconstructionFrame InverseSolveStepFem(FEMMesh mesh, FEMBoundaryCondition bc, double[] currentMeasurement, double gradientStepSize);
        ReconstructionFrame InverseSolveStepLbm(LBMGrid mesh, LBMBoundaryCondition bc, double[] currentMeasurement);
        ReconstructionFrame InverseSolveStepLbmCuda(LBMGrid mesh, LBMBoundaryCondition bc, double[] currentMeasurement);

        ReconstructionResult InverseSolveFem(int maxIterationCount, double gradientStepSize, double redularizationStepSize, double excitationAmplitude, double tolerance = 1e-6);
        ReconstructionResult InverseSolveLbm(int maxIterationCount, double gradientStepSize, double redularizationStepSize, double excitationAmplitude, double tolerance = 1e-6);
        ReconstructionResult InverseSolveLbmCuda(int maxIterationCount, double gradientStepSize, double redularizationStepSize, double excitationAmplitude, double tolerance = 1e-6);
        FEMMesh SolveGraphForward(FEMMesh mesh);
        ReconstructionResult InverseSolveStepGraph(FEMMesh mesh, double[] measurement, BoundaryCondition boundaryCondition, double stepSize);

        void SaveReconstruction(List<ReconstructionResult> frames, string name, ReconstructionRuntimeContext parameters);
        IEnumerable<ReconstructionInfo> GetReconstructions();
        List<ReconstructionResult> LoadReconstruction(string filePath);
    }
}
