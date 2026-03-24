using System.Globalization;

namespace Utility.Classes.Configurations.ReconstructionConfiguration
{
    /// <summary>
    /// Serializable snapshot of the reconstruction canvas layout and editable connection state.
    /// </summary>
    public sealed record ReconstructionCanvasSnapshot(
        IReadOnlyList<ReconstructionCanvasBlockSnapshot> Blocks,
        IReadOnlyList<ReconstructionCanvasConnectionSnapshot> Connections);

    /// <summary>
    /// Serializable representation of a single block on the reconstruction canvas.
    /// </summary>
    public sealed record ReconstructionCanvasBlockSnapshot(
        string Id,
        BlockType Type,
        double X,
        double Y,
        double FontSize,
        double Width,
        double Height,
        double Rotation,
        IReadOnlyList<ReconstructionCanvasParameterSnapshot> Parameters);

    /// <summary>
    /// Serializable representation of a block parameter.
    /// </summary>
    public sealed record ReconstructionCanvasParameterSnapshot(string Key, string Value);

    /// <summary>
    /// Serializable representation of a connection between two blocks.
    /// </summary>
    public sealed record ReconstructionCanvasConnectionSnapshot(
        string SourceId,
        string TargetId,
        double Weight,
        double ControlOffset1X,
        double ControlOffset1Y,
        double ControlOffset2X,
        double ControlOffset2Y);

    /// <summary>
    /// Helper for building portable canvas snapshots from live UI objects or persisted runtime configurations.
    /// </summary>
    public static class ReconstructionCanvasSnapshotBuilder
    {
        private const double DefaultControlOffset = 60.0;
        private const double DefaultFontSize = 13.0;

        public static ReconstructionCanvasSnapshot Create(IEnumerable<ReconstructionConfigurationBlock> blocks,
                                                         IEnumerable<ReconstructionConnection> connections)
        {
            return new ReconstructionCanvasSnapshot(
                blocks.Select(block => new ReconstructionCanvasBlockSnapshot(
                    block.Id,
                    block.Type,
                    block.X,
                    block.Y,
                    block.FontSize,
                    block.Width,
                    block.Height,
                    block.Rotation,
                    block.Parameters.Select(parameter => new ReconstructionCanvasParameterSnapshot(
                        parameter.Key,
                        GetParameterValue(parameter)))
                        .ToList()))
                    .ToList(),
                connections.Select(connection => new ReconstructionCanvasConnectionSnapshot(
                    connection.Source.Id,
                    connection.Target.Id,
                    connection.Weight,
                    connection.ControlOffset1X,
                    connection.ControlOffset1Y,
                    connection.ControlOffset2X,
                    connection.ControlOffset2Y))
                    .ToList());
        }

        public static ReconstructionCanvasSnapshot Create(CompleteReconstructionConfiguration configuration,
                                                         IEnumerable<ReconstructionConfigurationBlock>? liveBlocks = null)
        {
            var liveBlockMap = liveBlocks?
                .GroupBy(block => block.Id)
                .ToDictionary(group => group.Key, group => group.First());

            return new ReconstructionCanvasSnapshot(
                configuration.Blocks
                    .Select(block =>
                    {
                        var fontSize = liveBlockMap != null && liveBlockMap.TryGetValue(block.Id, out var liveBlock)
                            ? liveBlock.FontSize
                            : DefaultFontSize;

                        return new ReconstructionCanvasBlockSnapshot(
                            block.Id,
                            block.Type,
                            block.X,
                            block.Y,
                            fontSize,
                            block.Width,
                            block.Height,
                            block.Rotation,
                            block.Parameters
                                .Select(parameter => new ReconstructionCanvasParameterSnapshot(parameter.Key, parameter.Value))
                                .ToList());
                    })
                    .ToList(),
                configuration.AllConnections
                    .Select(connection => new ReconstructionCanvasConnectionSnapshot(
                        connection.SourceId,
                        connection.TargetId,
                        connection.Weight,
                        DefaultControlOffset,
                        0.0,
                        -DefaultControlOffset,
                        0.0))
                    .ToList());
        }

        private static string GetParameterValue(ConfigurationParameter parameter) => parameter switch
        {
            TextParameter text => text.Value,
            NumberParameter number => number.Value.ToString("G17", CultureInfo.InvariantCulture),
            BoolParameter toggle => toggle.Value.ToString(CultureInfo.InvariantCulture),
            ChoiceParameter choice => choice.SelectedOption,
            _ => string.Empty
        };
    }
}
