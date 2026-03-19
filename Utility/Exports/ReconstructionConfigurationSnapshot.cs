using System;
using System.Collections.Generic;
using Utility.Classes.Reconstruction.VirtualElectrodes;

namespace Utility.Exports;

public sealed record ReconstructionConfigurationSnapshot(
    DateTime ExportedOn,
    ReconstructionParameterSection Reconstruction,
    WorkspaceParameterSection Workspace,
    MeasurementConfigurationSnapshot Measurement,
    MeshConfigurationSnapshot Mesh,
    InitialDistributionSnapshot InitialDistribution,
    VirtualElectrodeConfigurationSnapshot VirtualElectrodes);

public sealed record ReconstructionParameterSection(
    string DifferentialEquationSolver,
    string RegularizationTechnique,
    string ErrorMetric,
    string NumericSolver,
    string NumericOptimizer,
    string DrivePattern,
    int DrivePatternSkip,
    bool UsePotentialDifferences,
    bool UseOmpParallelization,
    bool UseCudaAcceleration,
    string MeasurementNoiseType,
    double MeasurementNoiseAmplitude);

public sealed record WorkspaceParameterSection(
    int MaxIterationCount,
    double StepSize,
    double RegularizationWeight,
    double ConductivityMinimumBound,
    double ConductivityMaximumBound);

public sealed record MeasurementConfigurationSnapshot(
    string MeasurementSource,
    string ElectrodeMeasurementSetup,
    string? MeasurementLabel,
    bool UsePotentialDifferences,
    bool HasPattern,
    string? Representation,
    int? SanitizedLength,
    int ChannelCount,
    IReadOnlyList<MeasurementChannelSnapshot> Channels);

public sealed record MeasurementChannelSnapshot(int TargetIndex, int FirstElectrodeIndex, int SecondElectrodeIndex);

public sealed record MeshConfigurationSnapshot(
    string MeshType,
    string Generator,
    int ElementCount,
    int ElectrodeCount,
    DateTime CreatedOn,
    string? SourceFileName,
    IReadOnlyDictionary<string, string> Parameters);

public sealed record InitialDistributionSnapshot(
    string InitialDistributionType,
    bool HasDistribution,
    int SampleCount,
    double Minimum,
    double Maximum,
    double Mean,
    double StandardDeviation);

public sealed record VirtualElectrodeConfigurationSnapshot(
    bool UseVirtualElectrodes,
    VirtualElectrodeMethod Method,
    int VirtualElectrodesPerGap,
    double LinearCombinationAlpha,
    double HarrachLambda,
    int NdMaxMode);
