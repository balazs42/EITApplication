using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Utility.Classes.Application;
using Utility.Classes.ReconstructionParameters;

namespace ElectricalImpedanceTomography.ViewModels
{
    public partial class MainPageViewModel : BaseViewModel
    {
        [ObservableProperty]
        private string debugLog = string.Empty;

        [ObservableProperty]
        private EITReconstructionParameters reconstructionParameters = Workspace.GetReconstructionParameters();

        partial void OnReconstructionParametersChanged(EITReconstructionParameters value)
        {
            Workspace.SetReconstructionParameters(value);
        }

        public IEnumerable<DifferentialEquationSolver> Solvers => Enum.GetValues<DifferentialEquationSolver>();
        public IEnumerable<RegularizationTechnique> Regularizations => Enum.GetValues<RegularizationTechnique>();
        public IEnumerable<ErrorMetric> ErrorMetrics => Enum.GetValues<ErrorMetric>();
        public IEnumerable<NumericOptimizer> NumericOptimizers => Enum.GetValues<NumericOptimizer>();
        public IEnumerable<NumericSolver> NumericSolvers => Enum.GetValues<NumericSolver>();

        //public IAsyncRelayCommand<string> NavigateCommand { get; }
        //public IAsyncRelayCommand LoadMeasurementCommand { get; }
        //public IAsyncRelayCommand LoadMeshCommand { get; }

        public MainPageViewModel()
        {
            foreach (var entry in Workspace.GetMessages())
                DebugLog += $"{entry.Key:HH:mm:ss} >> {entry.Value}\n";

            Workspace.MessageAdded += OnWorkspaceMessageAdded;

            //  NavigateCommand = new AsyncRelayCommand<string>(async (route) => await Shell.Current.GoToAsync(route));
            //  LoadMeasurementCommand = new AsyncRelayCommand(async () => await Task.CompletedTask);
            //  LoadMeshCommand = new AsyncRelayCommand(async () => await Task.CompletedTask);
        }

        private void OnWorkspaceMessageAdded(DateTime time, string message)
        {
            DebugLog += $"{time:HH:mm:ss} >> {message}\n";
        }

        public void OnLoadMeasurementClicked(object sender, EventArgs e)
        {

        }

        public void OnLoadMeshClicked(object sender, EventArgs e)
        {

        }
    }
}
