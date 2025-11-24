using CommunityToolkit.Mvvm.ComponentModel;
using System.Collections.ObjectModel;
using System.Linq;
using Utility.Classes.Factories;
using Utility.Classes.Measurement;
using Utility.Classes.ReconstructionParameters;

namespace Utility.Classes.Configurations.ReconstructionConfiguration
{
    /// <summary>
    /// Represents a node (block) in the reconstruction configuration graph.
    /// Stores visual properties and a collection of specific parameters.
    /// </summary>
    public class ReconstructionConfigurationBlock : ObservableObject
    {
        /// <summary>
        /// Unique identifier for the block instance.
        /// </summary>
        public string Id { get; } = Guid.NewGuid().ToString();

        /// <summary>
        /// The display title of the block.
        /// </summary>
        public string Title { get; set; }

        /// <summary>
        /// The functional type of the block.
        /// </summary>
        public BlockType Type { get; set; }

        /// <summary>
        /// Hex color code for the block's UI representation.
        /// </summary>
        public string IconColor { get; set; }

        private string _highlightedOption = string.Empty;
        /// <summary>
        /// Short text shown on the block card to reflect the key selection (e.g., chosen solver).
        /// </summary>
        public string HighlightedOption { get => _highlightedOption; set => SetProperty(ref _highlightedOption, value); }

        private double _x;
        /// <summary>
        /// X coordinate on the canvas.
        /// </summary>
        public double X { get => _x; set => SetProperty(ref _x, value); }

        private double _y;
        /// <summary>
        /// Y coordinate on the canvas.
        /// </summary>
        public double Y { get => _y; set => SetProperty(ref _y, value); }

        private bool _isSelected;
        /// <summary>
        /// Indicates whether the block is currently selected in the editor.
        /// </summary>
        public bool IsSelected { get => _isSelected; set => SetProperty(ref _isSelected, value); }

        /// <summary>
        /// Collection of configurable parameters associated with this block.
        /// </summary>
        public ObservableCollection<ConfigurationParameter> Parameters { get; set; } = new();

        /// <summary>
        /// Initializes a new instance of the <see cref="ReconstructionConfigurationBlock"/> class.
        /// </summary>
        /// <param name="title">The display title.</param>
        /// <param name="type">The block type.</param>
        /// <param name="x">Initial X position.</param>
        /// <param name="y">Initial Y position.</param>
        public ReconstructionConfigurationBlock(string title, BlockType type, double x, double y)
        {
            Title = title;
            Type = type;
            X = x;
            Y = y;
            SetColor();
            InitializeDefaultParameters();
            HookParameterChanges();
            UpdateHighlightedOption();
        }

        /// <summary>
        /// Sets the icon color based on the block type for visual distinction.
        /// </summary>
        private void SetColor()
        {
            IconColor = Type switch
            {
                BlockType.Initialization => "#FFD166", // Yellow
                BlockType.Measurement => "#4ECDC4",    // Teal
                BlockType.Solver => "#06D6A0",         // Green
                BlockType.Regularizer => "#118AB2",    // Blue
                BlockType.ErrorMetric => "#EF476F",    // Red
                BlockType.Optimizer => "#9D4EDD",      // Purple
                BlockType.PostProcessing => "#073B4C", // Dark Blue
                _ => "#CCCCCC"
            };
        }

        /// <summary>
        /// Populates the <see cref="Parameters"/> collection with default values based on the <see cref="BlockType"/>.
        /// The parameters are derived from the advanced methods described in the thesis.
        /// </summary>
        private void InitializeDefaultParameters()
        {
            switch (Type)
            {
                case BlockType.Initialization:
                    Parameters.Add(new ChoiceParameter
                    {
                        Name = "Initial Distribution",
                        Key = "init_method",
                        Options = Enum.GetNames(typeof(InitialDistributionTypes)).Select(FriendlyName).ToList(),
                        SelectedOption = FriendlyName(nameof(InitialDistributionTypes.Homogeneous))
                    });
                    Parameters.Add(new NumberParameter { Name = "Random Max Conductivity", Key = "rand_max", Value = 1.0, Min = 0.0 });
                    Parameters.Add(new NumberParameter { Name = "Slight Scaling", Key = "slight_scale", Value = 0.95, Min = 0.0, Max = 1.0, Step = 0.05 });
                    Parameters.Add(new NumberParameter { Name = "Random Differing Count", Key = "rand_diff_count", Value = 5, Min = 0, Step = 1 });
                    break;

                case BlockType.Solver:
                    Parameters.Add(new ChoiceParameter
                    {
                        Name = "Differential Equation Solver",
                        Key = "solver_type",
                        Options = Enum.GetNames(typeof(DifferentialEquationSolver)).Select(FriendlyName).ToList(),
                        SelectedOption = FriendlyName(nameof(DifferentialEquationSolver.FEM))
                    });
                    Parameters.Add(new ChoiceParameter
                    {
                        Name = "Numeric Solver",
                        Key = "numeric_solver",
                        Options = Enum.GetNames(typeof(NumericSolver)).Select(FriendlyName).ToList(),
                        SelectedOption = FriendlyName(nameof(NumericSolver.GMRES))
                    });
                    Parameters.Add(new NumberParameter { Name = "FEM Order", Key = "fem_order", Value = 1, Min = 1, Max = 2, Step = 1 });
                    Parameters.Add(new NumberParameter { Name = "LBM Relaxation Time (tau)", Key = "lbm_tau", Value = 0.51, Min = 0.50001, Step = 0.01 });
                    break;

                case BlockType.Measurement:
                    Parameters.Add(new ChoiceParameter
                    {
                        Name = "Measurement Source",
                        Key = "measurement_source",
                        Options = Enum.GetNames(typeof(MeasurementSourceOption)).Select(FriendlyName).ToList(),
                        SelectedOption = FriendlyName(nameof(MeasurementSourceOption.Simulated))
                    });
                    Parameters.Add(new ChoiceParameter
                    {
                        Name = "Electrode Setup",
                        Key = "electrode_setup",
                        Options = Enum.GetNames(typeof(ElectrodeMeasurementSetup)).Select(FriendlyName).ToList(),
                        SelectedOption = FriendlyName(nameof(ElectrodeMeasurementSetup.Active))
                    });
                    Parameters.Add(new BoolParameter { Name = "Use Potential Differences", Key = "use_potential_differences", Value = false });
                    Parameters.Add(new BoolParameter { Name = "Apply Measurement Noise", Key = "apply_noise", Value = false });
                    Parameters.Add(new ChoiceParameter
                    {
                        Name = "Noise Type",
                        Key = "noise_type",
                        Options = Enum.GetNames(typeof(MeasurementNoiseType)).Select(FriendlyName).ToList(),
                        SelectedOption = FriendlyName(nameof(MeasurementNoiseType.None))
                    });
                    Parameters.Add(new NumberParameter { Name = "Noise Amplitude / dB", Key = "noise_amplitude", Value = 0.0, Min = 0.0, Step = 0.1 });
                    break;

                case BlockType.Regularizer:
                    Parameters.Add(new ChoiceParameter
                    {
                        Name = "Regularization Technique",
                        Key = "reg_tech",
                        Options = Enum.GetNames(typeof(RegularizationTechnique)).Select(FriendlyName).ToList(),
                        SelectedOption = FriendlyName(nameof(RegularizationTechnique.FirstOrderTikhonov))
                    });
                    Parameters.Add(new NumberParameter { Name = "Lambda (Regularization Weight)", Key = "lambda", Value = 0.01, Min = 0, Step = 0.001 });
                    Parameters.Add(new BoolParameter { Name = "Spatial Weighting", Key = "spatial_weighting", Value = false });
                    break;

                case BlockType.ErrorMetric:
                    Parameters.Add(new ChoiceParameter
                    {
                        Name = "Error Metric",
                        Key = "metric_type",
                        Options = Enum.GetNames(typeof(ErrorMetric)).Select(FriendlyName).ToList(),
                        SelectedOption = FriendlyName(nameof(ErrorMetric.Wasserstein2))
                    });
                    Parameters.Add(new BoolParameter { Name = "Use ROI Focusing", Key = "use_roi", Value = false });
                    break;

                case BlockType.Optimizer:
                    Parameters.Add(new ChoiceParameter
                    {
                        Name = "Optimization Algorithm",
                        Key = "opt_algo",
                        Options = Enum.GetNames(typeof(NumericOptimizer)).Select(FriendlyName).ToList(),
                        SelectedOption = FriendlyName(nameof(NumericOptimizer.ADAM))
                    });
                    Parameters.Add(new NumberParameter { Name = "Max Iterations", Key = "max_iter", Value = 50, Min = 1, Step = 10 });
                    Parameters.Add(new NumberParameter { Name = "Convergence Tolerance", Key = "conv_tol", Value = 1e-4, Min = 1e-9 });
                    Parameters.Add(new NumberParameter { Name = "Step Size (Learning Rate)", Key = "step_size", Value = 0.01, Step = 0.001 });
                    break;

                case BlockType.PostProcessing:
                    Parameters.Add(new ChoiceParameter { Name = "Filter Type", Key = "post_filter", Options = new List<string> { "None", "Median", "Gaussian", "Anisotropic Diffusion" }, SelectedOption = "Median" });
                    Parameters.Add(new ChoiceParameter { Name = "Morphological Operation", Key = "morph", Options = new List<string> { "None", "Erosion", "Dilatation", "Opening", "Closing" }, SelectedOption = "None" });
                    Parameters.Add(new NumberParameter { Name = "Kernel Size", Key = "kernel_size", Value = 3, Min = 1, Step = 2 });
                    break;
            }
        }

        private static string FriendlyName(string raw)
            => string.Concat(raw.Select((c, i) => i > 0 && char.IsUpper(c) && !char.IsUpper(raw[i - 1]) ? $" {c}" : c.ToString()));

        private void HookParameterChanges()
        {
            foreach (var choice in Parameters.OfType<ChoiceParameter>())
            {
                choice.PropertyChanged += (_, args) =>
                {
                    if (args.PropertyName == nameof(ChoiceParameter.SelectedOption))
                        UpdateHighlightedOption();
                };
            }
        }

        private void UpdateHighlightedOption()
        {
            HighlightedOption = Parameters.OfType<ChoiceParameter>().FirstOrDefault()?.SelectedOption ?? Type.ToString();
        }
    }
}
