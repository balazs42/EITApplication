namespace ElectricalImpedanceTomography.Views;
public partial class SpotifyMiniPlayerPage : ContentPage
{
    public SpotifyMiniPlayerPage(SpotifyMiniPlayerViewModel vm)
    {
        InitializeComponent();
        BindingContext = vm;

        VolSlider.ValueChanged += (_, e) =>
        {
            vm.VolumeChanged((int)e.NewValue);
        };
    }

    protected override void OnAppearing()
    {
        base.OnAppearing();
        (BindingContext as SpotifyMiniPlayerViewModel)!.OnShow();
    }

    protected override void OnDisappearing()
    {
        (BindingContext as SpotifyMiniPlayerViewModel)!.OnHide();
        base.OnDisappearing();
    }

    protected override void OnHandlerChanged()
    {
        base.OnHandlerChanged();
#if WINDOWS
        // make it a compact “widget” sized window
        this.Window?.SetSizeWindows(360, 180);
#endif
    }
}
