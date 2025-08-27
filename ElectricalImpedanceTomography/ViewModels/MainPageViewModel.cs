using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Utility.Classes.ReconstructionParameters;

namespace ElectricalImpedanceTomography.ViewModels
{
    public partial class MainPageViewModel : BaseViewModel
    {
        [ObservableProperty]
        private string debugLog = string.Empty;

        [ObservableProperty]
        private EITReconstructionParameters reconstructionParameters = new();

        public static IEnumerable<DifferentialEquationSolver> Solvers => Enum.GetValues<DifferentialEquationSolver>();
        public static IEnumerable<RegularizationTechnique> Regularizations => Enum.GetValues<RegularizationTechnique>();
        public static IEnumerable<ErrorMetric> ErrorMetrics => Enum.GetValues<ErrorMetric>();
        public static IEnumerable<NumericOptimizer> NumericOptimizers => Enum.GetValues<NumericOptimizer>();
        public static IEnumerable<NumericSolver> NumericSolvers => Enum.GetValues<NumericSolver>();

        //public IAsyncRelayCommand<string> NavigateCommand { get; }
        //public IAsyncRelayCommand LoadMeasurementCommand { get; }
        //public IAsyncRelayCommand LoadMeshCommand { get; }

        public MainPageViewModel()
        {
            //  NavigateCommand = new AsyncRelayCommand<string>(async (route) => await Shell.Current.GoToAsync(route));
            //  LoadMeasurementCommand = new AsyncRelayCommand(async () => await Task.CompletedTask);
            //  LoadMeshCommand = new AsyncRelayCommand(async () => await Task.CompletedTask);
        }

        public void OnLoadMeasurementsClicked(object sender, EventArgs e)
        {

        }

        public void OnLoadMeshClicked(object sender, EventArgs e)
        {

        }
    }
}
