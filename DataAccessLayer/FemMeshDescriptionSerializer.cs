using System.Text.Json;
using Utility.Classes;
using Utility.Classes.Application;
using Utility.Classes.Discretizer;
using Utility.Classes.Discretizer.FiniteElementMesh;
using Utility.Classes.Measurement;
using Utility.Exports;

namespace DataAccessLayer;

public static class FemMeshDescriptionSerializer
{
    public const string SchemaVersion = "eit-fem-description/v1";

    public static FemMeshDescription Create(FEMMesh mesh,
                                            string name,
                                            string? stlFileName,
                                            ReconstructionContinuationSnapshot? reconstruction = null)
    {
        if (mesh == null)
            throw new ArgumentNullException(nameof(mesh));

        var metadata = mesh.Metadata ?? new DiscretizationMetaData();
        var metadataSnapshot = new DiscretizationMetadataSnapshot(
            metadata.CreatedOn,
            metadata.Generator ?? string.Empty,
            metadata.ElementCount > 0 ? metadata.ElementCount : mesh.ElementsTyped.Count,
            metadata.Parameters != null
                ? new Dictionary<string, string>(metadata.Parameters)
                : new Dictionary<string, string>());

        var vertices = mesh.Vertices
            .OrderBy(vertex => vertex.GlobalId)
            .Select(vertex => new FemVertexStateSnapshot(
                vertex.GlobalId,
                vertex.X,
                vertex.Y,
                vertex.Potential,
                vertex.IsBoundary,
                vertex.IsElectrode,
                vertex.BoundaryId,
                vertex.ElectrodeId))
            .ToList();

        var elements = mesh.ElementsTyped
            .OrderBy(element => element.Id)
            .Select(element => new FemElementStateSnapshot(
                element.Id,
                element.Vertices[0].GlobalId,
                element.Vertices[1].GlobalId,
                element.Vertices[2].GlobalId,
                element.Conductivity))
            .ToList();

        var electrodes = mesh.ElectrodesTyped
            .OrderBy(electrode => electrode.Id)
            .Select(electrode => new FemElectrodeStateSnapshot(
                electrode.Id,
                electrode.MeshId,
                electrode.FEMVertexIds?.ToList() ?? new List<int>(),
                electrode.Current,
                electrode.ZContact,
                electrode.Potential,
                electrode.IsExcitation,
                electrode.IsGround,
                electrode.IsMeasuring,
                electrode.PointElectrode,
                electrode.IsVirtual,
                electrode.Length))
            .ToList();

        return new FemMeshDescription(
            SchemaVersion,
            name,
            stlFileName,
            new FemMeshStateSnapshot(metadataSnapshot, vertices, elements, electrodes),
            reconstruction);
    }

    public static void Write(string stlPath, FemMeshDescription description)
    {
        var jsonPath = GetDescriptionPath(stlPath);
        var options = new JsonSerializerOptions
        {
            WriteIndented = true
        };

        File.WriteAllText(jsonPath, JsonSerializer.Serialize(description, options));
    }

    public static FemMeshDescription? TryRead(string path)
    {
        string descriptionPath;
        if (string.Equals(Path.GetExtension(path), ".json", StringComparison.OrdinalIgnoreCase))
        {
            descriptionPath = path;
        }
        else
        {
            descriptionPath = GetDescriptionPath(path);
        }

        if (!File.Exists(descriptionPath))
            return null;

        try
        {
            var description = JsonSerializer.Deserialize<FemMeshDescription>(File.ReadAllText(descriptionPath));
            return IsRecognizedDescription(description) ? description : null;
        }
        catch
        {
            return null;
        }
    }

    public static FEMMesh RehydrateMesh(FemMeshDescription description)
    {
        if (description == null)
            throw new ArgumentNullException(nameof(description));

        var meshState = description.Mesh ?? throw new InvalidOperationException("Mesh description is missing mesh state.");

        var vertexMap = meshState.Vertices
            .ToDictionary(
                vertex => vertex.GlobalId,
                vertex => new FEMVertex
                {
                    GlobalId = vertex.GlobalId,
                    X = vertex.X,
                    Y = vertex.Y,
                    Potential = vertex.Potential,
                    IsBoundary = vertex.IsBoundary,
                    IsElectrode = vertex.IsElectrode,
                    BoundaryId = vertex.BoundaryId,
                    ElectrodeId = vertex.ElectrodeId
                });

        var elements = meshState.Elements
            .OrderBy(element => element.Id)
            .Select(element => new FEMElement(
                element.Id,
                vertexMap[element.V1],
                vertexMap[element.V2],
                vertexMap[element.V3])
            {
                Conductivity = element.Conductivity
            })
            .ToList();

        var mesh = new FEMMesh(vertexMap.Values, elements);

        foreach (var vertexState in meshState.Vertices)
        {
            var vertex = mesh.GetVertexById(vertexState.GlobalId);
            vertex.X = vertexState.X;
            vertex.Y = vertexState.Y;
            vertex.Potential = vertexState.Potential;
            vertex.IsBoundary = vertexState.IsBoundary;
            vertex.IsElectrode = vertexState.IsElectrode;
            vertex.BoundaryId = vertexState.BoundaryId;
            vertex.ElectrodeId = vertexState.ElectrodeId;
        }

        mesh.Initialize();

        var electrodes = meshState.Electrodes
            .OrderBy(electrode => electrode.Id)
            .Select(electrode =>
            {
                var restored = new FEMElectrode(
                    electrode.Id,
                    electrode.FemVertexIds?.Count > 0 ? electrode.FemVertexIds : new[] { electrode.MeshId },
                    electrode.Current,
                    electrode.ZContact,
                    electrode.Potential,
                    electrode.IsExcitation,
                    electrode.IsGround,
                    electrode.IsMeasuring,
                    electrode.IsVirtual)
                {
                    PointElectrode = electrode.PointElectrode,
                    Length = electrode.Length
                };

                return restored;
            })
            .ToList();

        mesh.SetElectrodes(electrodes);
        if (electrodes.Count > 0)
            mesh.UpdateElectrodeLengths();

        var conductivity = meshState.Elements.ToDictionary(element => element.Id, element => element.Conductivity);
        mesh.SetConductivityDistribution(new ConductivityDistribution(conductivity));

        var potentials = meshState.Vertices.ToDictionary(vertex => vertex.GlobalId, vertex => vertex.Potential);
        mesh.SetPotentialDistribution(new PotentialDistribution(potentials));

        var metadata = meshState.Metadata;
        mesh.Metadata = new DiscretizationMetaData
        {
            CreatedOn = metadata.CreatedOn,
            Generator = metadata.Generator,
            ElementCount = metadata.ElementCount,
            Parameters = metadata.Parameters != null
                ? new Dictionary<string, string>(metadata.Parameters)
                : new Dictionary<string, string>()
        };

        if (!string.IsNullOrWhiteSpace(description.StlFileName))
            mesh.Metadata.Parameters["source"] = description.StlFileName!;
        mesh.Metadata.Parameters["descriptionSchema"] = SchemaVersion;

        ApplyImportedMeasurement(description.Reconstruction);

        return mesh;
    }

    public static string GetDescriptionPath(string stlPath)
    {
        var descriptionPath = Path.ChangeExtension(stlPath, ".json");
        if (string.IsNullOrWhiteSpace(descriptionPath))
            throw new InvalidOperationException("Unable to determine mesh description path.");

        return descriptionPath;
    }

    private static bool IsRecognizedDescription(FemMeshDescription? description)
        => description != null
           && string.Equals(description.SchemaVersion, SchemaVersion, StringComparison.OrdinalIgnoreCase)
           && description.Mesh != null;

    private static void ApplyImportedMeasurement(ReconstructionContinuationSnapshot? reconstruction)
    {
        Workspace.ClearImportedMeasurement();

        if (reconstruction?.MeasurementFrames == null || reconstruction.MeasurementFrames.Count == 0)
            return;

        var frames = reconstruction.MeasurementFrames
            .Select(frame => frame.Values?.ToArray() ?? Array.Empty<double>())
            .Where(frame => frame.Length > 0)
            .ToList();

        if (frames.Count == 0)
            return;

        var stepIndices = reconstruction.MeasurementFrames
            .Select(frame => frame.StepIndex)
            .ToList();

        EITMeasurement measurement = reconstruction.MeasurementCurrentAmplitude.HasValue
            ? new EITMeasurement(frames, reconstruction.MeasurementCurrentAmplitude.Value, stepIndices: stepIndices)
            : new EITMeasurement(frames, stepIndices: stepIndices);

        Workspace.SetImportedMeasurement(measurement, reconstruction.MeasurementLabel);
    }
}
