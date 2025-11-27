using System;
using System.Collections.Generic;
using System.Linq;

namespace Utility.Classes.Configurations.ReconstructionConfiguration
{
    /// <summary>
    /// Immutable snapshot of the entire reconstruction canvas configuration.
    /// Captures blocks, their parameters, and all relevant weighted links so the
    /// description can be passed to the runtime reconstruction pipeline.
    /// </summary>
    public class CompleteReconstructionConfiguration
    {
        public CompleteReconstructionConfiguration(
            IReadOnlyList<ConfiguredBlockSnapshot> blocks,
            IReadOnlyList<WeightedConnectionSnapshot> allConnections,
            IReadOnlyList<WeightedConnectionSnapshot> solverToErrorMetricWeights,
            IReadOnlyList<WeightedConnectionSnapshot> solverToRegularizerWeights,
            IReadOnlyList<WeightedConnectionSnapshot> optimizerInputWeights,
            IReadOnlyList<WeightedConnectionSnapshot> optimizerToModelWeights)
        {
            Blocks = blocks;
            AllConnections = allConnections;
            SolverToErrorMetricWeights = solverToErrorMetricWeights;
            SolverToRegularizerWeights = solverToRegularizerWeights;
            OptimizerInputWeights = optimizerInputWeights;
            OptimizerToModelWeights = optimizerToModelWeights;
            CapturedAtUtc = DateTime.UtcNow;
        }

        /// <summary>
        /// Complete set of configured blocks, including their parameters and layout metadata.
        /// </summary>
        public IReadOnlyList<ConfiguredBlockSnapshot> Blocks { get; }

        /// <summary>
        /// Every connection on the canvas with its persisted weight for auditing/debugging.
        /// </summary>
        public IReadOnlyList<WeightedConnectionSnapshot> AllConnections { get; }

        /// <summary>
        /// Weights applied from solver blocks into error metrics.
        /// </summary>
        public IReadOnlyList<WeightedConnectionSnapshot> SolverToErrorMetricWeights { get; }

        /// <summary>
        /// Weights applied from solver blocks into regularizers.
        /// </summary>
        public IReadOnlyList<WeightedConnectionSnapshot> SolverToRegularizerWeights { get; }

        /// <summary>
        /// Combined weights feeding each optimizer (from error metrics and regularizers).
        /// </summary>
        public IReadOnlyList<WeightedConnectionSnapshot> OptimizerInputWeights { get; }

        /// <summary>
        /// Weights applied from optimizers into the model block.
        /// </summary>
        public IReadOnlyList<WeightedConnectionSnapshot> OptimizerToModelWeights { get; }

        /// <summary>
        /// Timestamp describing when this snapshot was produced.
        /// </summary>
        public DateTime CapturedAtUtc { get; }
    }

    /// <summary>
    /// Lightweight DTO capturing a single block configuration and its parameters.
    /// </summary>
    public record ConfiguredBlockSnapshot(
        string Id,
        BlockType Type,
        string Title,
        double X,
        double Y,
        double Width,
        double Height,
        double Rotation,
        IReadOnlyList<BlockParameterSnapshot> Parameters);

    /// <summary>
    /// Serialized representation of a parameter for later reconstruction.
    /// </summary>
    public record BlockParameterSnapshot(string Key, string Name, string Value);

    /// <summary>
    /// Captures the directional and weighted characteristics of a connection.
    /// </summary>
    public record WeightedConnectionSnapshot(
        string SourceId,
        string TargetId,
        BlockType SourceType,
        BlockType TargetType,
        double Weight);

    /// <summary>
    /// Utility class that translates the live canvas graph into a portable configuration snapshot.
    /// </summary>
    public static class CompleteReconstructionConfigurationBuilder
    {
        /// <summary>
        /// Creates a full reconstruction description from the current blocks and connections.
        /// </summary>
        public static CompleteReconstructionConfiguration Create(
            IEnumerable<ReconstructionConfigurationBlock> blocks,
            IEnumerable<ReconstructionConnection> connections)
        {
            var blockSnapshots = blocks
                .Select(b => new ConfiguredBlockSnapshot(
                    b.Id,
                    b.Type,
                    b.Title,
                    b.X,
                    b.Y,
                    b.Width,
                    b.Height,
                    b.Rotation,
                    b.Parameters
                        .Select(p => new BlockParameterSnapshot(p.Key, p.Name, GetParameterValue(p)))
                        .ToList()))
                .ToList();

            var connectionSnapshots = connections
                .Select(c => new WeightedConnectionSnapshot(
                    c.Source.Id,
                    c.Target.Id,
                    c.Source.Type,
                    c.Target.Type,
                    c.Weight))
                .ToList();

            var solverToErrorWeights = connectionSnapshots
                .Where(c => c.SourceType == BlockType.Solver && c.TargetType == BlockType.ErrorMetric)
                .ToList();

            var solverToRegularizerWeights = connectionSnapshots
                .Where(c => c.SourceType == BlockType.Solver && c.TargetType == BlockType.Regularizer)
                .ToList();

            var optimizerInputWeights = connectionSnapshots
                .Where(c => c.TargetType == BlockType.Optimizer &&
                            (c.SourceType == BlockType.ErrorMetric || c.SourceType == BlockType.Regularizer))
                .Select(c => new WeightedConnectionSnapshot(c.SourceId,
                                                            c.TargetId,
                                                            c.SourceType,
                                                            c.TargetType,
                                                            1.0))
                .ToList();

            var optimizerToModelWeights = connectionSnapshots
                .Where(c => c.SourceType == BlockType.Optimizer && c.TargetType == BlockType.Model)
                .ToList();

            return new CompleteReconstructionConfiguration(
                blockSnapshots,
                connectionSnapshots,
                solverToErrorWeights,
                solverToRegularizerWeights,
                optimizerInputWeights,
                optimizerToModelWeights);
        }

        private static string GetParameterValue(ConfigurationParameter parameter) => parameter switch
        {
            TextParameter text => text.Value,
            NumberParameter number => number.Value.ToString(),
            BoolParameter toggle => toggle.Value.ToString(),
            ChoiceParameter choice => choice.SelectedOption,
            _ => string.Empty
        };
    }
}
