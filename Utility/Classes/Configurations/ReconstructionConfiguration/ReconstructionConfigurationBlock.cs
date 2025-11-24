using CommunityToolkit.Mvvm.ComponentModel;
using System.Collections.ObjectModel;

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
        }

        /// <summary>
        /// Sets the icon color based on the block type for visual distinction.
        /// </summary>
        private void SetColor()
        {
            IconColor = Type switch
            {
                BlockType.Initialization => "#FFD166", // Yellow
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
                        Name = "Initialization Method",
                        Key = "init_method",
                        Options = new List<string> { "Homogeneous", "Random", "CloseToTarget", "Pre-calculated (CIM)", "Phase-Informed (Takens)" },
                        SelectedOption = "Homogeneous"
                    });
                    Parameters.Add(new NumberParameter { Name = "Background Conductivity (S/m)", Key = "bg_cond", Value = 1.0, Min = 0.0001 });
                    Parameters.Add(new NumberParameter { Name = "Randomization Noise Level", Key = "rand_noise", Value = 0.1, Min = 0, Max = 1 });

                    // Parameters for Takens' Theorem phase-space reconstruction
                    Parameters.Add(new NumberParameter { Name = "Takens: Embedding Dimension (d)", Key = "takens_dim", Value = 6, Min = 1, Step = 1 });
                    Parameters.Add(new NumberParameter { Name = "Takens: Time Delay (tau)", Key = "takens_tau", Value = 5, Min = 1, Step = 1 });
                    break;

                case BlockType.Solver:
                    Parameters.Add(new ChoiceParameter
                    {
                        Name = "DE Solver Type",
                        Key = "solver_type",
                        Options = new List<string> { "FEM (Finite Element Method)", "LBM (Lattice Boltzmann Method)", "Graph-Based (CIM)" },
                        SelectedOption = "FEM"
                    });
                    Parameters.Add(new NumberParameter { Name = "FEM Order", Key = "fem_order", Value = 1, Min = 1, Max = 2, Step = 1 });

                    // Parameters for Lattice Boltzmann Method
                    Parameters.Add(new ChoiceParameter { Name = "LBM Lattice Structure", Key = "lbm_domain", Options = new List<string> { "D2Q9", "D3Q19" }, SelectedOption = "D2Q9" });
                    Parameters.Add(new NumberParameter { Name = "LBM Relaxation Time (tau)", Key = "lbm_tau", Value = 0.51, Min = 0.50001, Step = 0.01 });
                    break;

                case BlockType.Regularizer:
                    Parameters.Add(new ChoiceParameter
                    {
                        Name = "Regularization Technique",
                        Key = "reg_tech",
                        Options = new List<string> { "None", "Tikhonov (0-order)", "Tikhonov (1st-order)", "Total Variation (TV)", "Laplace", "Graph Consistency (Tether)", "D-Bar (Direct)" },
                        SelectedOption = "Tikhonov (1st-order)"
                    });
                    Parameters.Add(new ChoiceParameter { Name = "Weight Calculation Mode", Key = "weight_mode", Options = new List<string> { "Static", "L-Curve", "Heuristic", "Spatially Adaptive" }, SelectedOption = "Static" });
                    Parameters.Add(new NumberParameter { Name = "Lambda (Regularization Weight)", Key = "lambda", Value = 0.01, Min = 0, Step = 0.001 });

                    // Parameter for D-Bar method
                    Parameters.Add(new NumberParameter { Name = "D-Bar: Cutoff Frequency (k0)", Key = "dbar_cutoff", Value = 4.0, Min = 0.1 });

                    // Parameter for Graph Consistency regularization
                    Parameters.Add(new NumberParameter { Name = "Graph Tether Weight", Key = "graph_tether", Value = 0.1, Min = 0 });
                    break;

                case BlockType.ErrorMetric:
                    Parameters.Add(new ChoiceParameter
                    {
                        Name = "Error Metric",
                        Key = "metric_type",
                        Options = new List<string> { "L2 (Least Squares)", "Wasserstein-2 (Geometric)", "Wasserstein-2 (Conductivity Aware)", "Wasserstein-2 (Spectral)", "Energy-Based W2" },
                        SelectedOption = "Wasserstein-2 (Geometric)"
                    });

                    // Parameters for Conductivity Aware Wasserstein metric
                    Parameters.Add(new ChoiceParameter { Name = "Ground Cost Type", Key = "ground_cost", Options = new List<string> { "Euclidean", "Geodesic (Weighted)", "Effective Resistance" }, SelectedOption = "Euclidean" });
                    Parameters.Add(new NumberParameter { Name = "Geodesic Beta Exponent", Key = "geo_beta", Value = 1.0, Min = 0.1 });

                    // Parameter for Spectral Wasserstein metric
                    Parameters.Add(new NumberParameter { Name = "Spectral Eigenvalues Count (r)", Key = "spec_r", Value = 10, Min = 1, Step = 1 });

                    // Parameter for Convex combination of metrics
                    Parameters.Add(new NumberParameter { Name = "Convex Weight (Alpha)", Key = "alpha", Value = 0.5, Min = 0, Max = 1, Step = 0.1 });

                    // Parameter for Energy-Based metric with ROI
                    Parameters.Add(new BoolParameter { Name = "Use ROI Focusing", Key = "use_roi", Value = false });
                    break;

                case BlockType.Optimizer:
                    Parameters.Add(new ChoiceParameter
                    {
                        Name = "Optimization Algorithm",
                        Key = "opt_algo",
                        Options = new List<string> { "Gradient Descent", "Gauss-Newton", "L-BFGS", "ADAM", "Nesterov Accelerated Gradient" },
                        SelectedOption = "ADAM"
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
    }
}
