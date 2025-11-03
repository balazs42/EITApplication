using System;
using System.Collections.Generic;
using System.Reflection;
using BusinessLayer;
using ServiceLayer;
using Utility.Classes;
using Utility.Classes.Application;
using Utility.Classes.Discretizer;
using Utility.Classes.Discretizer.FiniteElementMesh;
using Utility.Classes.Discretizer.LatticeBoltzmannGrid;
using Utility.Classes.Measurement;
using Utility.Classes.ReconstructionParameters;
using Utility.Classes.VirtualElectrodes;
using Utility.Logger;
using Xunit;

namespace Utility.Tests;

public class ReconstructionServiceVirtualElectrodeTests
{
    private sealed class TestElectrode : Electrode
    {
        public TestElectrode(int id, bool isVirtual = false)
        {
            Id = id;
            IsVirtual = isVirtual;
            IsMeasuring = true;
        }
    }

    private sealed class NullPersistence : IReconstructionPersistence
    {
        public void SetConductivityDistributions(ConductivityDistribution original, ConductivityDistribution initial) => throw new NotImplementedException();
        public void InitializeReconstruction(IDiscretization discretization, EITReconstructionParameters parameters, bool reinit) => throw new NotImplementedException();
        public ReconstructionFrame Step(double[] measurement, BoundaryCondition boundaryCondition, double gradientStepSize, double redularizationStepSize) => throw new NotImplementedException();
        public void Run(int maxIterationCount, double gradientStepSize, double redularizationStepSize) => throw new NotImplementedException();
        public ReconstructionResult Stop() => throw new NotImplementedException();
        public PotentialDistribution ForwardSolveStepFem() => throw new NotImplementedException();
        public PotentialDistribution ForwardSolveStepLbm() => throw new NotImplementedException();
        public PotentialDistribution ForwardSolveStepLbmCuda() => throw new NotImplementedException();
        public ReconstructionFrame InverseSolveStepFem(FEMMesh mesh, FEMBoundaryCondition bc, double[] currentMeasurement, double gradientStepSize) => throw new NotImplementedException();
        public ReconstructionFrame InverseSolveStepLbm(LBMGrid mesh, LBMBoundaryCondition bc, double[] currentMeasurement) => throw new NotImplementedException();
        public ReconstructionFrame InverseSolveStepLbmCuda(LBMGrid mesh, LBMBoundaryCondition bc, double[] currentMeasurement) => throw new NotImplementedException();
        public ReconstructionResult InverseSolveFem(int maxIterationCount, double gradientStepSize, double redularizationStepSize, double excitationAmplitude, double tolerance = 1e-6) => throw new NotImplementedException();
        public ReconstructionResult InverseSolveLbm(int maxIterationCount, double gradientStepSize, double redularizationStepSize, double excitationAmplitude, double tolerance = 1e-6) => throw new NotImplementedException();
        public ReconstructionResult InverseSolveLbmCuda(int maxIterationCount, double gradientStepSize, double redularizationStepSize, double excitationAmplitude, double tolerance = 1e-6) => throw new NotImplementedException();
        public List<double[]> SimulateFemMeasurements(FEMMesh mesh, double excitationAmplitude, DrivePattern drivePattern) => throw new NotImplementedException();
        public EITMeasurement SimulateLbmMeasurements(LBMGrid mesh, double excitationAmplitude, DrivePattern drivePattern) => throw new NotImplementedException();
        public FEMMesh SolveGraphForward(FEMMesh mesh) => throw new NotImplementedException();
        public ReconstructionResult InverseSolveStepGraph(FEMMesh mesh, double[] measurement, BoundaryCondition boundaryCondition, double stepSize) => throw new NotImplementedException();
        public void SaveReconstruction(List<ReconstructionResult> frames, string name, EITReconstructionParameters parameters) => throw new NotImplementedException();
        public IEnumerable<ReconstructionInfo> GetReconstructions() => throw new NotImplementedException();
        public List<ReconstructionResult> LoadReconstruction(string filePath) => throw new NotImplementedException();
    }

    private sealed class NullLogger : ILogger
    {
        public void LogError(string error) { }
        public void LogInfo(string info) { }
        public void LogWarning(string warning) { }
    }

    [Fact]
    public void PrepareMeasurementFrame_CompletesVirtualElectrodeValues()
    {
        var parameters = new EITReconstructionParameters();
        parameters.VirtualElectrodeSettings.UseVirtualElectrodes = true;
        parameters.VirtualElectrodeSettings.Method = VirtualElectrodeMethod.GeometricInterpolation;
        Workspace.SetReconstructionParameters(parameters);

        var service = new ReconstructionService(new NullPersistence(), new NullLogger());

        var electrodes = new List<Electrode>
        {
            new TestElectrode(0),
            new TestElectrode(1, isVirtual: true),
            new TestElectrode(2),
            new TestElectrode(3, isVirtual: true),
            new TestElectrode(4)
        };

        double[] measurement = { 10.0, 20.0, 30.0 };

        var method = typeof(ReconstructionService).GetMethod(
            "PrepareMeasurementFrame",
            BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("PrepareMeasurementFrame method not found");

        var sanitized = (double[])method.Invoke(service, new object[] { measurement, electrodes })!;

        Assert.Equal(5, sanitized.Length);
        Assert.Equal(10.0, sanitized[0], 6);
        Assert.Equal(20.0, sanitized[2], 6);
        Assert.Equal(30.0, sanitized[4], 6);
        Assert.Equal(15.0, sanitized[1], 6);
        Assert.Equal(25.0, sanitized[3], 6);
    }
}
