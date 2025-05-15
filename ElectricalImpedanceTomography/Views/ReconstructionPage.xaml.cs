using ElectricalImpedanceTomography.ViewModels;

namespace ElectricalImpedanceTomography.Views;

public partial class ReconstructionPage : ContentPage
{
	private readonly ReconstructionPageViewModel _viewModel;

	public ReconstructionPage()
	{
		InitializeComponent();

		_viewModel = Utility.Composition.Container.ResolveObject<ReconstructionPageViewModel>();

		BindingContext = _viewModel;

        //InitializePickers();
	}

    public void InitializePickers()
    {
        var DESolverList = new List<string>() { "Finite Element Method", "Lattice Boltzmann Method" };
        DESolverPicker.ItemsSource = DESolverList;

        var RegularaizationTechniqueList = new List<string>() { "None", "Zero-Order Tikhonov", "First-Order Tikhonov", "Total Variation", "Laplace" };
        RegulariaztionPicker.ItemsSource = RegularaizationTechniqueList;

        var ErrorMetricList = new List<string>() { "L2", "Wasserstein-2" };
        ErrorMetricPicker.ItemsSource = ErrorMetricList;

        var NumericSolverList = new List<string>() { "LU Decomposition", "SVD", "tSVD", "GMRES" };
        NumericSolverPicker.ItemsSource = NumericSolverList;

        var NumericOptimizerList = new List<string>() { "Gradient Based", "Particle Swarm" };
        NumericOptimizerPicker.ItemsSource = NumericOptimizerList;
    }

    private void OnStartReconstruction(object sender, EventArgs e)
    {
        _viewModel.OnStartReconstructionClicked(sender, e);
    }
}