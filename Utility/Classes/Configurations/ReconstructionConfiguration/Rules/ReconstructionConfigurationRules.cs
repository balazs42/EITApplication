using System.Collections.Generic;
using System.Linq;

namespace Utility.Classes.Configurations.ReconstructionConfiguration.Rules
{
    public record BlockConnectionConstraint(int MaxInputs, int MaxOutputs)
    {
        public static BlockConnectionConstraint Unlimited { get; } = new(int.MaxValue, int.MaxValue);
    }

    /// <summary>
    /// Contains all editor-side constraints and validation logic for reconstruction configuration.
    /// Keep rules centralized here so they are easy to evolve.
    /// </summary>
    public static class ReconstructionConfigurationRules
    {
        private static readonly Dictionary<BlockType, BlockConnectionConstraint> ConnectionConstraints = new()
        {
            { BlockType.Initialization, new BlockConnectionConstraint(0, int.MaxValue) },
            { BlockType.Measurement, new BlockConnectionConstraint(0, int.MaxValue) },
            { BlockType.ErrorMetric, BlockConnectionConstraint.Unlimited },
            { BlockType.Optimizer, BlockConnectionConstraint.Unlimited },
            { BlockType.PostProcessing, BlockConnectionConstraint.Unlimited },
            { BlockType.Regularizer, BlockConnectionConstraint.Unlimited },
            { BlockType.Solver, BlockConnectionConstraint.Unlimited }
        };

        public static BlockConnectionConstraint GetConnectionConstraint(BlockType type) =>
            ConnectionConstraints.TryGetValue(type, out var constraint)
                ? constraint
                : BlockConnectionConstraint.Unlimited;

        public static bool HasAvailableInput(ReconstructionConfigurationBlock block, IEnumerable<ReconstructionConnection> connections)
        {
            var constraint = GetConnectionConstraint(block.Type);
            if (constraint.MaxInputs == int.MaxValue) return true;
            return connections.Count(c => c.Target == block) < constraint.MaxInputs;
        }

        public static bool HasAvailableOutput(ReconstructionConfigurationBlock block, IEnumerable<ReconstructionConnection> connections)
        {
            var constraint = GetConnectionConstraint(block.Type);
            if (constraint.MaxOutputs == int.MaxValue) return true;
            return connections.Count(c => c.Source == block) < constraint.MaxOutputs;
        }

        /// <summary>
        /// Validate current graph and return a list of actionable issues.
        /// </summary>
        public static IReadOnlyList<string> Validate(IEnumerable<ReconstructionConfigurationBlock> blocks, IEnumerable<ReconstructionConnection> connections)
        {
            var issues = new List<string>();

            foreach (var block in blocks)
            {
                var constraint = GetConnectionConstraint(block.Type);
                var incoming = connections.Count(c => c.Target == block);
                var outgoing = connections.Count(c => c.Source == block);

                if (constraint.MaxInputs < int.MaxValue && incoming > constraint.MaxInputs)
                {
                    issues.Add($"{block.Title} exceeds its allowed number of inputs ({constraint.MaxInputs}).");
                }

                if (constraint.MaxOutputs < int.MaxValue && outgoing > constraint.MaxOutputs)
                {
                    issues.Add($"{block.Title} exceeds its allowed number of outputs ({constraint.MaxOutputs}).");
                }
            }

            foreach (var errorMetric in blocks.Where(b => b.Type == BlockType.ErrorMetric))
            {
                var hasMeasurement = connections.Any(c => c.Target == errorMetric && c.Source.Type == BlockType.Measurement);
                if (!hasMeasurement)
                {
                    issues.Add($"Error Metric '{errorMetric.Title}' must be connected to a Measurement block.");
                }
            }

            return issues;
        }
    }
}

