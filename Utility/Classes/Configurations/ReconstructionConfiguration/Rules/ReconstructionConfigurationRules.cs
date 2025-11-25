using System.Collections.Generic;
using System.Linq;
using Utility.Classes.Application;
using Utility.Classes.Discretizer.FiniteElementMesh;
using Utility.Classes.Discretizer.LatticeBoltzmannGrid;
using Utility.Classes.ReconstructionParameters;

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
            { BlockType.Model, BlockConnectionConstraint.Unlimited },
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

        public static bool IsConnectionAllowed(BlockType source, BlockType target, out string? reason)
        {
            reason = null;

            if (source == BlockType.Initialization && target != BlockType.Model)
            {
                reason = "Initializer output must feed the Model block.";
                return false;
            }

            if (source == BlockType.Measurement && target != BlockType.ErrorMetric)
            {
                reason = "Measurement outputs can only feed Error Metric blocks.";
                return false;
            }

            if (target == BlockType.ErrorMetric && source != BlockType.Solver && source != BlockType.Measurement)
            {
                reason = "Error Metric inputs must originate from a Solver or Measurement block.";
                return false;
            }

            if (target == BlockType.Regularizer && source != BlockType.Model)
            {
                reason = "Regularizer inputs must come from the Model block.";
                return false;
            }

            if (source == BlockType.Optimizer && target != BlockType.Model)
            {
                reason = "Optimizer outputs must connect into the Model block.";
                return false;
            }

            if (source == BlockType.Regularizer && target != BlockType.Optimizer)
            {
                reason = "Regularizer outputs must connect into an Optimizer block.";
                return false;
            }

            if (target == BlockType.Optimizer && source != BlockType.ErrorMetric && source != BlockType.Regularizer)
            {
                reason = "Only Error Metric or Regularizer blocks can connect to an Optimizer.";
                return false;
            }

            return true;
        }

        public static bool IsConnectionAllowed(BlockType source, BlockType target) => IsConnectionAllowed(source, target, out _);

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

            foreach (var connection in connections)
            {
                if (!IsConnectionAllowed(connection.Source.Type, connection.Target.Type, out var reason) && reason != null)
                {
                    issues.Add($"{connection.Source.Title} -> {connection.Target.Title}: {reason}");
                }
            }

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

            foreach (var optimizer in blocks.Where(b => b.Type == BlockType.Optimizer))
            {
                if (!connections.Any(c => c.Source == optimizer && c.Target.Type == BlockType.Model))
                {
                    issues.Add($"Optimizer '{optimizer.Title}' must feed the Model block.");
                }
            }

            foreach (var errorMetric in blocks.Where(b => b.Type == BlockType.ErrorMetric))
            {
                var hasMeasurement = connections.Any(c => c.Target == errorMetric && c.Source.Type == BlockType.Measurement);
                if (!hasMeasurement)
                {
                    issues.Add($"Error Metric '{errorMetric.Title}' must be connected to a Measurement block.");
                }

                var hasSolver = connections.Any(c => c.Target == errorMetric && c.Source.Type == BlockType.Solver);
                if (!hasSolver)
                {
                    issues.Add($"Error Metric '{errorMetric.Title}' must be connected to a Solver block.");
                }
            }

            foreach (var regularizer in blocks.Where(b => b.Type == BlockType.Regularizer))
            {
                var hasModelInput = connections.Any(c => c.Target == regularizer && c.Source.Type == BlockType.Model);
                if (!hasModelInput)
                {
                    issues.Add($"Regularizer '{regularizer.Title}' must be connected to the Model block.");
                }

                var hasOptimizerOutput = connections.Any(c => c.Source == regularizer && c.Target.Type == BlockType.Optimizer);
                if (!hasOptimizerOutput)
                {
                    issues.Add($"Regularizer '{regularizer.Title}' must feed into an Optimizer block.");
                }
            }

            foreach (var type in Enum.GetValues<BlockType>().Where(t => t != BlockType.PostProcessing && t != BlockType.Regularizer))
            {
                if (!blocks.Any(b => b.Type == type))
                {
                    issues.Add($"Add at least one {type} block to the configuration.");
                }
            }

            var modelCount = blocks.Count(b => b.Type == BlockType.Model);
            if (modelCount > 1)
            {
                issues.Add("Only one Model block is allowed in the configuration.");
            }

            var discretization = Workspace.GetDiscretization();
            if (discretization is FEMMesh)
            {
                foreach (var solver in blocks.Where(b => b.Type == BlockType.Solver))
                {
                    if (GetSelectedSolverType(solver) != DifferentialEquationSolver.FEM)
                    {
                        issues.Add("FEM mesh detected: choose FEM as the solver.");
                    }
                }
            }
            else if (discretization is LBMGrid)
            {
                foreach (var solver in blocks.Where(b => b.Type == BlockType.Solver))
                {
                    if (GetSelectedSolverType(solver) != DifferentialEquationSolver.LBM)
                    {
                        issues.Add("LBM grid detected: choose LBM as the solver.");
                    }
                }
            }

            return issues;
        }

        private static DifferentialEquationSolver GetSelectedSolverType(ReconstructionConfigurationBlock block)
        {
            var solverChoice = block.Parameters.OfType<ChoiceParameter>().FirstOrDefault(p => p.Key == "solver_type");
            return solverChoice != null
                ? ParseEnum<DifferentialEquationSolver>(solverChoice.SelectedOption)
                : DifferentialEquationSolver.FEM;
        }

        private static T ParseEnum<T>(string friendly) where T : struct, Enum
        {
            if (string.IsNullOrWhiteSpace(friendly))
                return default;

            static string Normalize(string value) =>
                new string(value.Where(char.IsLetterOrDigit).ToArray()).ToUpperInvariant();

            var normalizedInput = Normalize(friendly);

            foreach (var name in Enum.GetNames(typeof(T)))
            {
                if (Normalize(name) == normalizedInput)
                    return Enum.Parse<T>(name, true);
            }

            return Enum.TryParse<T>(friendly, true, out var parsed)
                ? parsed
                : default;
        }
    }
}

