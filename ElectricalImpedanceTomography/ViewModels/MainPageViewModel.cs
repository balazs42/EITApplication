using System;
using System.Collections.Generic;
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

        public IEnumerable<DifferentialEquationSolver> Solvers => Enum.GetValues<DifferentialEquationSolver>();
        public IEnumerable<RegularizationTechnique> Regularizations => Enum.GetValues<RegularizationTechnique>();

        public IAsyncRelayCommand<string> NavigateCommand { get; }
        public IAsyncRelayCommand LoadMeasurementCommand { get; }
        public IAsyncRelayCommand LoadMeshCommand { get; }

        public MainPageViewModel()
        {
            NavigateCommand = new AsyncRelayCommand<string>(async (route) => await Shell.Current.GoToAsync(route));
            LoadMeasurementCommand = new AsyncRelayCommand(async () => await Task.CompletedTask);
            LoadMeshCommand = new AsyncRelayCommand(async () => await Task.CompletedTask);
        }
    }
}
