using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Maui.ApplicationModel;
using System.Collections.ObjectModel;
using Utility.Classes.Application;
using Utility.Classes.ReconstructionParameters;

namespace ElectricalImpedanceTomography.ViewModels
{
    public partial class MainPageViewModel : BaseViewModel
    {
        public ObservableCollection<WorkspaceMessage> DebugLog { get; } = new();

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
                DebugLog.Add(entry);

            Workspace.MessageAdded += OnWorkspaceMessageAdded;

            //  NavigateCommand = new AsyncRelayCommand<string>(async (route) => await Shell.Current.GoToAsync(route));
            //  LoadMeasurementCommand = new AsyncRelayCommand(async () => await Task.CompletedTask);
            //  LoadMeshCommand = new AsyncRelayCommand(async () => await Task.CompletedTask);
        }

        private void OnWorkspaceMessageAdded(WorkspaceMessage message)
        {
            MainThread.BeginInvokeOnMainThread(() => DebugLog.Add(message));
        }

        public void OnLoadMeasurementClicked(object sender, EventArgs e)
        {

        }

        public void OnLoadMeshClicked(object sender, EventArgs e)
        {

        }
    }
}
