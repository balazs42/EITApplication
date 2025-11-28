using Utility.Classes.Application;
using Utility.Classes.Configurations.ReconstructionConfiguration;
using Utility.Classes.Discretizer.FiniteElementMesh;
using Utility.Classes.Factories;
using Utility.Classes.Measurement;
using Utility.Classes;
using Utility.Classes.ReconstructionParameters;

namespace BusinessLayer
{
    /// <summary>
    /// Translates the block/connection based reconstruction configuration into
    /// the concrete runtime objects required by the FEM reconstruction pipeline.
    /// </summary>
    public static class ReconstructionConfigurationMaterializer
    {
        public static ReconstructionRuntimeContext Materialize(CompleteReconstructionConfiguration configuration)
        {
            if (configuration == null)
                throw new ArgumentNullException(nameof(configuration));

            var parameters = new ReconstructionRuntimeContext();

            // Extract block parameters
            foreach (var block in configuration.Blocks)
            {
                switch (block.Type)
                {
                    case BlockType.Initialization:
                        ApplyInitializationParameters(block, parameters);
                        break;
                    case BlockType.Model:
                        ApplyModelParameters(block, parameters);
                        break;
                    case BlockType.Measurement:
                        ApplyMeasurementParameters(block, parameters);
                        break;
                    case BlockType.Solver:
                        ApplySolverParameters(block, parameters);
                        break;
                    case BlockType.Regularizer:
                        ApplyRegularizerParameters(block, parameters);
                        break;
                    case BlockType.ErrorMetric:
                        ApplyErrorMetricParameters(block, parameters);
                        break;
                    case BlockType.Optimizer:
                        ApplyOptimizerParameters(block, parameters);
                        break;
                }
            }

            var mesh = Workspace.GetDiscretization() as FEMMesh
                ?? throw new InvalidOperationException("FEM mesh is required before starting reconstruction.");

            var numericSolver = NumericSolverFactory.Create(parameters.NumericSolver, parameters.UseOmpParallelization, parameters.UseCudaAcceleration);
            var differentialEquationSolver = DifferentialEquationSolverFactory.Create(mesh, parameters.DifferentialEquationSolver, numericSolver);

            // Capture the current mesh conductivities as the immutable "target"/original distribution
            // and apply the user-selected initialization distribution to the mesh so reconstruction starts
            // from that state instead of the target.
            var originalDistribution = new ConductivityDistribution(mesh.GetConductivityDistribution().Conductivities);
            var initialDistribution = BuildInitialDistribution(mesh, parameters, configuration.Blocks);

            // Ensure the reconstruction begins from the configured initial distribution instead of the
            // mesh's original conductivities. Copy the generated distribution into the mesh so all
            // downstream components (measurement simulation, gradients, etc.) operate on the correct
            // starting state.
            mesh.SetConductivityDistribution(new ConductivityDistribution(initialDistribution.Conductivities));

            var errorMetrics = configuration.Blocks
                .Where(b => b.Type == BlockType.ErrorMetric)
                .Select(block =>
                {
                    double weight = configuration.SolverToErrorMetricWeights
                        .Where(c => c.TargetId == block.Id)
                        .Sum(c => c.Weight);
                    weight = weight == 0 ? 1.0 : weight;
                    return (block.Id, weight, ErrorMetricFactory.Create(ParseEnum<ErrorMetric>(GetParameterValue(block, "metric_type"))));
                })
                .ToList();

            var regularizers = configuration.Blocks
                .Where(b => b.Type == BlockType.Regularizer)
                .Select(block =>
                {
                    double weight = configuration.SolverToRegularizerWeights
                        .Where(c => c.TargetId == block.Id)
                        .Sum(c => c.Weight);
                    weight = weight == 0 ? 1.0 : weight;
                    var technique = ParseEnum<RegularizationTechnique>(GetParameterValue(block, "reg_tech"));
                    return (block.Id, weight, RegularizationFactory.Create(technique, mesh));
                })
                .ToList();

            var optimizers = configuration.Blocks
                .Where(b => b.Type == BlockType.Optimizer)
                .Select(block =>
                {
                    double weight = configuration.OptimizerToModelWeights
                        .Where(c => c.SourceId == block.Id)
                        .Sum(c => c.Weight);
                    weight = weight == 0 ? 1.0 : weight;
                    var optimizer = NumericOptimizerFactory.Create(ParseEnum<NumericOptimizer>(GetParameterValue(block, "opt_algo")), null);
                    return (block.Id, weight, optimizer);
                })
                .ToList();

            parameters.RuntimeMesh = mesh;
            parameters.RuntimeDifferentialEquationSolver = differentialEquationSolver;
            parameters.RuntimeNumericSolver = numericSolver;
            parameters.Regularizers = regularizers;
            parameters.ErrorMetrics = errorMetrics;
            parameters.NumericOptimizers = optimizers;
            parameters.OriginalDistribution = originalDistribution;
            parameters.InitialDistribution = initialDistribution;
            parameters.MeasurementSetup = Workspace.GetElectrodeMeasurementSetup();
            parameters.AllConnections = configuration.AllConnections;

            return parameters;
        }

        private static void ApplyInitializationParameters(ConfiguredBlockSnapshot block, ReconstructionRuntimeContext parameters)
        {
            var parsed = ParseEnum<InitialDistributionTypes>(GetParameterValue(block, "init_method"));

            if (Enum.IsDefined(typeof(InitialDistributionTypes), parsed))
                parameters.InitialDistributionType = parsed;
        }

        private static void ApplyModelParameters(ConfiguredBlockSnapshot block, ReconstructionRuntimeContext parameters)
        {
            parameters.ConductivityMinimumBound = ParseDouble(GetParameterValue(block, "sigma_min"), parameters.ConductivityMinimumBound);
            parameters.ConductivityMaximumBound = ParseDouble(GetParameterValue(block, "sigma_max"), parameters.ConductivityMaximumBound);
            parameters.ContactImpedanceOhms = ParseDouble(GetParameterValue(block, "contact_impedance"), parameters.ContactImpedanceOhms);
            parameters.ContactImpedanceVariation = ParseDouble(GetParameterValue(block, "contact_impedance_var"), parameters.ContactImpedanceVariation);
        }

        private static void ApplyMeasurementParameters(ConfiguredBlockSnapshot block, ReconstructionRuntimeContext parameters)
        {
            parameters.UsePotentialDifferences = ParseBool(GetParameterValue(block, "use_potential_differences"));
            Workspace.SetElectrodeMeasurementSetup(ParseEnum<ElectrodeMeasurementSetup>(GetParameterValue(block, "electrode_setup")));
        }

        private static void ApplySolverParameters(ConfiguredBlockSnapshot block, ReconstructionRuntimeContext parameters)
        {
            parameters.DifferentialEquationSolver = ParseEnum<DifferentialEquationSolver>(GetParameterValue(block, "solver_type"));
            parameters.NumericSolver = ParseEnum<NumericSolver>(GetParameterValue(block, "numeric_solver"));
        }

        private static void ApplyRegularizerParameters(ConfiguredBlockSnapshot block, ReconstructionRuntimeContext parameters)
        {
            parameters.RegularizationTechnique = ParseEnum<RegularizationTechnique>(GetParameterValue(block, "reg_tech"));
        }

        private static void ApplyErrorMetricParameters(ConfiguredBlockSnapshot block, ReconstructionRuntimeContext parameters)
        {
            parameters.ErrorMetric = ParseEnum<ErrorMetric>(GetParameterValue(block, "metric_type"));
        }

        private static void ApplyOptimizerParameters(ConfiguredBlockSnapshot block, ReconstructionRuntimeContext parameters)
        {
            parameters.NumericOptimizer = ParseEnum<NumericOptimizer>(GetParameterValue(block, "opt_algo"));
        }

        private static ConductivityDistribution BuildInitialDistribution(FEMMesh mesh, ReconstructionRuntimeContext parameters, IReadOnlyList<ConfiguredBlockSnapshot> blocks)
        {
            var initBlock = blocks.FirstOrDefault(b => b.Type == BlockType.Initialization);

            double max = ParseDouble(GetParameterValue(initBlock, "rand_max"), 1.0);
            double scale = ParseDouble(GetParameterValue(initBlock, "slight_scale"), 0.95);
            int differing = (int)ParseDouble(GetParameterValue(initBlock, "rand_diff_count"), 5);

            return parameters.InitialDistributionType switch
            {
                InitialDistributionTypes.Homogeneous => ConductivityDistributionFactory.CreateHomogeneous(mesh),
                InitialDistributionTypes.Random => ConductivityDistributionFactory.CreateRandom(mesh, max),
                InitialDistributionTypes.SlightlyDiffering => ConductivityDistributionFactory.CreateSlightlyDiffering(mesh, scale),
                InitialDistributionTypes.RandomSlightlyDiffering => ConductivityDistributionFactory.CreateRandomSlightlyDiffering(mesh, differing, scale),
                InitialDistributionTypes.CloseToTarget => ConductivityDistributionFactory.CreateCloseToTarget(mesh),
                _ => ConductivityDistributionFactory.CreateHomogeneous(mesh)
            };
        }

        private static string GetParameterValue(ConfiguredBlockSnapshot? block, string key)
        {
            var parameter = block?.Parameters.FirstOrDefault(p => p.Key == key);
            return parameter?.Value ?? string.Empty;
        }

        private static double ParseDouble(string value, double fallback)
            => double.TryParse(value, out var parsed) ? parsed : fallback;

        private static bool ParseBool(string value)
            => bool.TryParse(value, out var parsed) && parsed;

        private static T ParseEnum<T>(string value) where T : struct, Enum
        {
            if (string.IsNullOrWhiteSpace(value))
                return default;

            static string Normalize(string input)
                => new string(input.Where(char.IsLetterOrDigit).ToArray()).ToUpperInvariant();

            var normalized = Normalize(value);

            foreach (var name in Enum.GetNames(typeof(T)))
            {
                if (Normalize(name) == normalized)
                    return Enum.Parse<T>(name, true);
            }

            return Enum.TryParse<T>(value, true, out var parsed) ? parsed : default;
        }
    }

}
