using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Maui.ApplicationModel;
using Microsoft.Maui.Graphics;
using ServiceLayer;
using System.Collections.ObjectModel;
using Utility.Classes.Application;
using Utility.Classes.ReconstructionParameters;

namespace ElectricalImpedanceTomography.ViewModels
{
    public partial class MainPageViewModel : BaseViewModel
    {
        private readonly IDAQService _daqService;

        [ObservableProperty]
        private ObservableCollection<WorkspaceMessage> debugLog = new();

        [ObservableProperty]
        private double currentAmplitude;

        [ObservableProperty]
        private double excitationFrequency;

        [ObservableProperty]
        private bool hardwareConnected;

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

        public IAsyncRelayCommand<string> NavigateCommand { get; }

        public string HardwareConnectionText => HardwareConnected ? "Connected" : "Disconnected";
        public Color HardwareStatusColor => HardwareConnected ? Colors.Green : Colors.Red;

        partial void OnHardwareConnectedChanged(bool value)
        {
            OnPropertyChanged(nameof(HardwareConnectionText));
            OnPropertyChanged(nameof(HardwareStatusColor));
        }

        public MainPageViewModel(IDAQService daqService)
        {
            _daqService = daqService;

            foreach (var entry in Workspace.GetMessages())
                DebugLog.Add(entry);

            Workspace.MessageAdded += OnWorkspaceMessageAdded;

            NavigateCommand = new AsyncRelayCommand<string>(async (route) => await Shell.Current.GoToAsync(route));

            UpdateHardwareInfo();
        }

        private void OnWorkspaceMessageAdded(WorkspaceMessage message)
        {
            MainThread.BeginInvokeOnMainThread(() => DebugLog.Add(message));
        }

        private void UpdateHardwareInfo()
        {
            try
            {
                var meas = _daqService.GetEITMeasurement();
                HardwareConnected = true;
                CurrentAmplitude = meas.CurrentAmplitude ?? 0;
                ExcitationFrequency = 0; // frequency not provided by hardware yet
            }
            catch
            {
                HardwareConnected = false;
                CurrentAmplitude = 0;
                ExcitationFrequency = 0;
            }
        }

        public void OnLoadMeasurementClicked(object sender, EventArgs e)
        {

        }

        public void OnLoadMeshClicked(object sender, EventArgs e)
        {

        }
    }
}
