namespace ElectricalImpedanceTomography.Views;
public sealed class SpotifyMiniPlayerWindowService
{
    private Microsoft.Maui.Controls.Window? _miniWindow;

    public void ShowOrActivate()
    {
        if (_miniWindow is null || _miniWindow.Handler is null)
        {
            var page = Utility.Composition.Container.ResolveObject<SpotifyMiniPlayerPage>();
            _miniWindow = new Microsoft.Maui.Controls.Window(page)
            {
                Title = "Spotify Mini Player"
            };

            _miniWindow.Destroying += (_, _) => _miniWindow = null;

            global::Microsoft.Maui.Controls.Application.Current?.OpenWindow(_miniWindow);
            return;
        }

#if WINDOWS
        _miniWindow.BringToFrontWindows();
#endif
    }
}
