using Utility.Classes.Configurations.ReconstructionConfiguration;

namespace Utility.Exports;

public sealed record FemMeshDescription(
    string SchemaVersion,
    string Name,
    string? StlFileName,
    FemMeshStateSnapshot Mesh,
    ReconstructionContinuationSnapshot? Reconstruction);

public sealed record FemMeshStateSnapshot(
    DiscretizationMetadataSnapshot Metadata,
    IReadOnlyList<FemVertexStateSnapshot> Vertices,
    IReadOnlyList<FemElementStateSnapshot> Elements,
    IReadOnlyList<FemElectrodeStateSnapshot> Electrodes);

public sealed record DiscretizationMetadataSnapshot(
    DateTime CreatedOn,
    string Generator,
    int ElementCount,
    IReadOnlyDictionary<string, string> Parameters);

public sealed record FemVertexStateSnapshot(
    int GlobalId,
    double X,
    double Y,
    double Potential,
    bool IsBoundary,
    bool IsElectrode,
    int BoundaryId,
    int ElectrodeId);

public sealed record FemElementStateSnapshot(
    int Id,
    int V1,
    int V2,
    int V3,
    double Conductivity);

public sealed record FemElectrodeStateSnapshot(
    int Id,
    int MeshId,
    IReadOnlyList<int> FemVertexIds,
    double Current,
    double ZContact,
    double Potential,
    bool IsExcitation,
    bool IsGround,
    bool IsMeasuring,
    bool PointElectrode,
    bool IsVirtual,
    double Length);

public sealed record ReconstructionContinuationSnapshot(
    ReconstructionConfigurationSnapshot Configuration,
    ReconstructionCanvasSnapshot? Canvas,
    string MeasurementSource,
    string? MeasurementLabel,
    string DrivePattern,
    int DrivePatternSkip,
    double? MeasurementCurrentAmplitude,
    IReadOnlyList<MeasurementFrameSnapshot>? MeasurementFrames,
    ConductivityDistributionSnapshot? OriginalDistribution,
    ConductivityDistributionSnapshot? InitialDistribution,
    ConductivityDistributionSnapshot? CurrentDistribution);

public sealed record MeasurementFrameSnapshot(int StepIndex, IReadOnlyList<double> Values);

public sealed record ConductivityDistributionSnapshot(IReadOnlyDictionary<int, double> Values);
