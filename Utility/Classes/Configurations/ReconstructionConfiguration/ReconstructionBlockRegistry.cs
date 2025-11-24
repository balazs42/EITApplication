using System;
using System.Collections.Generic;
using System.Linq;
using Utility.Classes.Application;
using Utility.Classes.Discretizer;
using Utility.Classes.Measurement;
using Utility.Classes.ReconstructionParameters;

namespace Utility.Classes.Configurations.ReconstructionConfiguration
{
    /// <summary>
    /// Central catalog that defines which reconstruction blocks are available,
    /// how they are rendered, and how they synchronize with the workspace.
    /// </summary>
    public static class ReconstructionBlockRegistry
    {
        private static readonly Dictionary<BlockType, ReconstructionBlockDefinition> Definitions =
            new(CreateDefaultDefinitions().ToDictionary(def => def.Type, def => def));

        public static IReadOnlyCollection<BlockType> BlockTypes => Definitions.Keys;

        public static ReconstructionConfigurationBlock CreateBlock(BlockType type, double x, double y, string? id = null)
        {
            if (!Definitions.TryGetValue(type, out var definition))
                throw new ArgumentException($"No block definition found for type {type}.", nameof(type));

            var parameters = definition.ParameterFactory.Invoke();
            return new ReconstructionConfigurationBlock(
                id ?? Guid.NewGuid().ToString(),
                definition.Title,
                type,
                definition.IconColor,
                x,
                y,
                parameters);
        }

        public static void ApplyBlocksToWorkspace(IEnumerable<ReconstructionConfigurationBlock> blocks)
        {
            var parameters = Workspace.GetReconstructionParameters();

            foreach (var block in blocks)
            {
                if (Definitions.TryGetValue(block.Type, out var definition))
                {
                    definition.ApplyParameters(block, parameters);
                }
            }

            Workspace.SetReconstructionParameters(parameters);
            Workspace.SetReconstructionBlocks(blocks.ToList());
        }

        public static ReconstructionBlockDefinition GetDefinition(BlockType type) => Definitions[type];

        private static IEnumerable<ReconstructionBlockDefinition> CreateDefaultDefinitions()
        {
            yield return new ReconstructionBlockDefinition(
                BlockType.Initialization,
                "Initialization",
                "#FFD166",
                () =>
                {
                    return new List<ConfigurationParameter>
                    {
                        new ChoiceParameter
                        {
                            Name = "Initial Distribution",
                            Key = "init_method",
                            Options = Enum.GetNames(typeof(InitialDistributionTypes)).Select(FriendlyName).ToList(),
                            SelectedOption = FriendlyName(nameof(InitialDistributionTypes.Homogeneous))
                        },
                        new NumberParameter { Name = "Random Max Conductivity", Key = "rand_max", Value = 1.0, Min = 0.0 },
                        new NumberParameter { Name = "Slight Scaling", Key = "slight_scale", Value = 0.95, Min = 0.0, Max = 1.0, Step = 0.05 },
                        new NumberParameter { Name = "Random Differing Count", Key = "rand_diff_count", Value = 5, Min = 1, Step = 1 }
                    };
                },
                (block, target) =>
                {
                    var selected = block.Parameters.OfType<ChoiceParameter>().FirstOrDefault(p => p.Key == "init_method");
                    if (selected != null)
                        target.InitialDistributionType = ParseEnum<InitialDistributionTypes>(selected.SelectedOption);
                });

            yield return new ReconstructionBlockDefinition(
                BlockType.Measurement,
                "Measurement",
                "#4ECDC4",
                () => new List<ConfigurationParameter>
                {
                    new ChoiceParameter
                    {
                        Name = "Measurement Source",
                        Key = "measurement_source",
                        Options = Enum.GetNames(typeof(MeasurementSourceOption)).Select(FriendlyName).ToList(),
                        SelectedOption = FriendlyName(nameof(MeasurementSourceOption.Simulated))
                    },
                    new ChoiceParameter
                    {
                        Name = "Electrode Setup",
                        Key = "electrode_setup",
                        Options = Enum.GetNames(typeof(ElectrodeMeasurementSetup)).Select(FriendlyName).ToList(),
                        SelectedOption = FriendlyName(nameof(ElectrodeMeasurementSetup.Active))
                    },
                    new BoolParameter { Name = "Use Potential Differences", Key = "use_potential_differences", Value = false },
                    new BoolParameter { Name = "Apply Measurement Noise", Key = "apply_noise", Value = false },
                    new ChoiceParameter
                    {
                        Name = "Noise Type",
                        Key = "noise_type",
                        Options = Enum.GetNames(typeof(MeasurementNoiseType)).Select(FriendlyName).ToList(),
                        SelectedOption = FriendlyName(nameof(MeasurementNoiseType.None))
                    },
                    new NumberParameter { Name = "Noise Amplitude / dB", Key = "noise_amplitude", Value = 0.0, Min = 0.0, Step = 0.1 }
                },
                (block, target) =>
                {
                    Workspace.SetMeasurementSource(ParseEnumFromChoice<MeasurementSourceOption>(block, "measurement_source"));
                    Workspace.SetElectrodeMeasurementSetup(ParseEnumFromChoice<ElectrodeMeasurementSetup>(block, "electrode_setup"));

                    var usePotential = block.Parameters.OfType<BoolParameter>().FirstOrDefault(p => p.Key == "use_potential_differences")?.Value ?? false;
                    target.UsePotentialDifferences = usePotential;

                    var noiseTypeChoice = block.Parameters.OfType<ChoiceParameter>().FirstOrDefault(p => p.Key == "noise_type");
                    var noiseType = noiseTypeChoice != null ? ParseEnum<MeasurementNoiseType>(noiseTypeChoice.SelectedOption) : MeasurementNoiseType.None;
                    target.MeasurementNoiseType = noiseType;

                    var noiseAmplitude = block.Parameters.OfType<NumberParameter>().FirstOrDefault(p => p.Key == "noise_amplitude")?.Value ?? 0.0;
                    var applyNoise = block.Parameters.OfType<BoolParameter>().FirstOrDefault(p => p.Key == "apply_noise")?.Value ?? false;
                    target.MeasurementNoiseAmplitude = applyNoise ? noiseAmplitude : 0.0;
                });

            yield return new ReconstructionBlockDefinition(
                BlockType.Solver,
                "Solver",
                "#06D6A0",
                () => new List<ConfigurationParameter>
                {
                    new ChoiceParameter
                    {
                        Name = "Differential Equation Solver",
                        Key = "solver_type",
                        Options = Enum.GetNames(typeof(DifferentialEquationSolver)).Select(FriendlyName).ToList(),
                        SelectedOption = FriendlyName(nameof(DifferentialEquationSolver.FEM))
                    },
                    new ChoiceParameter
                    {
                        Name = "Numeric Solver",
                        Key = "numeric_solver",
                        Options = Enum.GetNames(typeof(NumericSolver)).Select(FriendlyName).ToList(),
                        SelectedOption = FriendlyName(nameof(NumericSolver.GMRES))
                    },
                    new NumberParameter { Name = "FEM Order", Key = "fem_order", Value = 1, Min = 1, Max = 2, Step = 1 },
                    new NumberParameter { Name = "LBM Relaxation Time (tau)", Key = "lbm_tau", Value = 0.51, Min = 0.50001, Step = 0.01 }
                },
                (block, target) =>
                {
                    target.DifferentialEquationSolver = ParseEnumFromChoice<DifferentialEquationSolver>(block, "solver_type");
                    target.NumericSolver = ParseEnumFromChoice<NumericSolver>(block, "numeric_solver");
                    target.Mesh = target.DifferentialEquationSolver == DifferentialEquationSolver.FEM
                        ? DiscretizationType.FEM
                        : DiscretizationType.LBM;
                });

            yield return new ReconstructionBlockDefinition(
                BlockType.Regularizer,
                "Regularizer",
                "#118AB2",
                () => new List<ConfigurationParameter>
                {
                    new ChoiceParameter
                    {
                        Name = "Regularization Technique",
                        Key = "reg_tech",
                        Options = Enum.GetNames(typeof(RegularizationTechnique)).Select(FriendlyName).ToList(),
                        SelectedOption = FriendlyName(nameof(RegularizationTechnique.FirstOrderTikhonov))
                    },
                    new ChoiceParameter
                    {
                        Name = "Weight Mode",
                        Key = "reg_weight_mode",
                        Options = new List<string> { "Static", "D-Bar" },
                        SelectedOption = "Static"
                    },
                    new NumberParameter { Name = "Lambda (Regularization Weight)", Key = "lambda", Value = 0.01, Min = 0, Step = 0.001 },
                    new BoolParameter { Name = "Spatial Weighting", Key = "spatial_weighting", Value = false }
                },
                (block, target) =>
                {
                    target.RegularizationTechnique = ParseEnumFromChoice<RegularizationTechnique>(block, "reg_tech");
                    var lambdaParam = block.Parameters.OfType<NumberParameter>().FirstOrDefault(p => p.Key == "lambda");
                    var mode = block.Parameters.OfType<ChoiceParameter>().FirstOrDefault(p => p.Key == "reg_weight_mode")?.SelectedOption ?? "Static";

                    // Map weight selection back to the workspace. "D-Bar" can be used to signal
                    // a dynamic strategy; here we default to the provided lambda for static mode
                    // and fall back to a small stabilizer otherwise.
                    Workspace.RegularizationWeight = mode == "Static" ? lambdaParam?.Value ?? Workspace.RegularizationWeight : Math.Max(lambdaParam?.Value ?? 1e-3, 1e-4);
                });

            yield return new ReconstructionBlockDefinition(
                BlockType.ErrorMetric,
                "Error Metric",
                "#EF476F",
                () => new List<ConfigurationParameter>
                {
                    new ChoiceParameter
                    {
                        Name = "Error Metric",
                        Key = "metric_type",
                        Options = Enum.GetNames(typeof(ErrorMetric)).Select(FriendlyName).ToList(),
                        SelectedOption = FriendlyName(nameof(ErrorMetric.Wasserstein2))
                    },
                    new BoolParameter { Name = "Use ROI Focusing", Key = "use_roi", Value = false }
                },
                (block, target) =>
                {
                    target.ErrorMetric = ParseEnumFromChoice<ErrorMetric>(block, "metric_type");
                });

            yield return new ReconstructionBlockDefinition(
                BlockType.Optimizer,
                "Optimizer",
                "#9D4EDD",
                () => new List<ConfigurationParameter>
                {
                    new ChoiceParameter
                    {
                        Name = "Optimization Algorithm",
                        Key = "opt_algo",
                        Options = Enum.GetNames(typeof(NumericOptimizer)).Select(FriendlyName).ToList(),
                        SelectedOption = FriendlyName(nameof(NumericOptimizer.ADAM))
                    },
                    new NumberParameter { Name = "Max Iterations", Key = "max_iter", Value = 50, Min = 1, Step = 10 },
                    new NumberParameter { Name = "Convergence Tolerance", Key = "conv_tol", Value = 1e-4, Min = 1e-9 },
                    new NumberParameter { Name = "Step Size (Learning Rate)", Key = "step_size", Value = 0.01, Step = 0.001 }
                },
                (block, target) =>
                {
                    target.NumericOptimizer = ParseEnumFromChoice<NumericOptimizer>(block, "opt_algo");
                    Workspace.MaxIterationCount = (int)(block.Parameters.OfType<NumberParameter>().FirstOrDefault(p => p.Key == "max_iter")?.Value ?? Workspace.MaxIterationCount);
                    Workspace.StepSize = block.Parameters.OfType<NumberParameter>().FirstOrDefault(p => p.Key == "step_size")?.Value ?? Workspace.StepSize;
                });

            yield return new ReconstructionBlockDefinition(
                BlockType.PostProcessing,
                "Post-Processing",
                "#073B4C",
                () => new List<ConfigurationParameter>
                {
                    new ChoiceParameter { Name = "Filter Type", Key = "post_filter", Options = new List<string> { "None", "Median", "Gaussian", "Anisotropic Diffusion" }, SelectedOption = "Median" },
                    new ChoiceParameter { Name = "Morphological Operation", Key = "morph", Options = new List<string> { "None", "Erosion", "Dilatation", "Opening", "Closing" }, SelectedOption = "None" },
                    new NumberParameter { Name = "Kernel Size", Key = "kernel_size", Value = 3, Min = 1, Step = 2 }
                },
                (_, _) => { });
        }

        private static T ParseEnumFromChoice<T>(ReconstructionConfigurationBlock block, string key)
            where T : struct, Enum
        {
            var choice = block.Parameters.OfType<ChoiceParameter>().FirstOrDefault(p => p.Key == key);
            return choice == null ? default : ParseEnum<T>(choice.SelectedOption);
        }

        private static T ParseEnum<T>(string friendly) where T : struct, Enum
        {
            var raw = friendly.Replace(" ", string.Empty);
            if (Enum.TryParse<T>(raw, out var parsed))
                return parsed;

            return default;
        }

        private static string FriendlyName(string raw)
            => string.Concat(raw.Select((c, i) => i > 0 && char.IsUpper(c) && !char.IsUpper(raw[i - 1]) ? $" {c}" : c.ToString()));
    }
}
