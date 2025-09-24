using ElectricalImpedanceTomography.Extensions;
using ElectricalImpedanceTomography.ViewModels;
using Microsoft.Maui.Graphics;

namespace ElectricalImpedanceTomography.Views;

public partial class DAQPage : ContentPage
{
        private readonly DAQPageViewModel _viewModel;

        public DAQPage()
        {
                InitializeComponent();

                _viewModel = Utility.Composition.Container.ResolveObject<DAQPageViewModel>();

                BindingContext = _viewModel;
        }

    protected override void OnAppearing()
    {
        base.OnAppearing();
        var (startColor, endColor) = GetBackgroundPulseColors();
        this.StartBackgroundPulse(startColor, endColor);
    }

    protected override void OnDisappearing()
    {
        base.OnDisappearing();
        this.StopBackgroundPulse();
    }

    private static (Color Start, Color End) GetBackgroundPulseColors()
    {
        var theme = Application.Current?.RequestedTheme ?? AppTheme.Light;
        return theme == AppTheme.Dark
            ? (Color.FromArgb("#1A1426"), Color.FromArgb("#2A2140"))
            : (Color.FromArgb("#EADFFB"), Color.FromArgb("#DCD1F5"));
    }

    private void OnStopButtonPressed(object sender, EventArgs e)
    {

    }

    private void OnStartButtonPressed(object sender, EventArgs e)
    {

    }
}