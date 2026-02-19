using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using System.Text.Json;
using Utility.Classes.Spotify;

public partial class SpotifyMiniPlayerViewModel : ObservableObject
{
    private readonly SpotifySession _session;
    private readonly SpotifyPlayerApi _api;

    private CancellationTokenSource? _pollCts;
    private bool _isPinned;

    public SpotifyMiniPlayerViewModel(SpotifySession session, SpotifyPlayerApi api)
    {
        _session = session;
        _api = api;

        ConnectCommand = new AsyncRelayCommand(ConnectAsync);
        RefreshCommand = new AsyncRelayCommand(RefreshAsync);
        PlayPauseCommand = new AsyncRelayCommand(PlayPauseAsync);
        NextCommand = new AsyncRelayCommand(() => _api.NextAsync());
        PrevCommand = new AsyncRelayCommand(() => _api.PreviousAsync());
        TogglePinCommand = new RelayCommand(TogglePin);
        MinimizeCommand = new RelayCommand(MinimizeWindow);
    }

    [ObservableProperty] private string track = "-";
    [ObservableProperty] private string artist = "-";
    [ObservableProperty] private string status = "Not connected";
    [ObservableProperty] private int volumePercent = 50;
    [ObservableProperty] private bool isPlaying;

    public string PlayPauseText => IsPlaying ? "Pause" : "Play";
    public string ConnectText => Status.StartsWith("Connected") ? "Reconnect" : "Connect";
    public string PinText => _isPinned ? "📌" : "📍";

    public IAsyncRelayCommand ConnectCommand { get; }
    public IAsyncRelayCommand RefreshCommand { get; }
    public IAsyncRelayCommand PlayPauseCommand { get; }
    public IAsyncRelayCommand NextCommand { get; }
    public IAsyncRelayCommand PrevCommand { get; }
    public IRelayCommand TogglePinCommand { get; }
    public IRelayCommand MinimizeCommand { get; }

    public async void OnShow()
    {
        // Restore silently (no login UI)
        var ok = await _session.TryRestoreAsync();
        Status = ok ? "Connected (restored)" : "Not connected";
        OnPropertyChanged(nameof(ConnectText));

        StartPolling();
        if (ok) await RefreshAsync();
    }

    public void OnHide() => _pollCts?.Cancel();

    public void VolumeChanged(int v)
    {
        VolumePercent = v;
        _ = SetVolumeDebouncedAsync(v);
    }

    private CancellationTokenSource? _volCts;
    private async Task SetVolumeDebouncedAsync(int v)
    {
        _volCts?.Cancel();
        _volCts = new CancellationTokenSource();
        var ct = _volCts.Token;

        await Task.Delay(200, ct);
        await _api.SetVolumeAsync(v, ct);
    }

    private async Task ConnectAsync()
    {
        Status = "Connecting...";
        await _session.ConnectInteractiveAsync();
        Status = "Connected";
        OnPropertyChanged(nameof(ConnectText));
        await RefreshAsync();
    }

    private async Task PlayPauseAsync()
    {
        if (IsPlaying) await _api.PauseAsync();
        else await _api.PlaySafeAsync();

        await RefreshAsync();
        OnPropertyChanged(nameof(PlayPauseText));
    }

    private void StartPolling()
    {
        _pollCts?.Cancel();
        _pollCts = new CancellationTokenSource();
        var ct = _pollCts.Token;

        _ = Task.Run(async () =>
        {
            while (!ct.IsCancellationRequested)
            {
                try { await RefreshAsync(); }
                catch { /* keep UI stable */ }

                await Task.Delay(1200, ct);
            }
        }, ct);
    }

    private async Task RefreshAsync()
    {
        var json = await _api.GetPlaybackAsync();

        if (json is null)
        {
            Track = "-";
            Artist = "-";
            IsPlaying = false;
            Status = "No active device / nothing playing";
            OnPropertyChanged(nameof(PlayPauseText));
            return;
        }

        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        IsPlaying = root.TryGetProperty("is_playing", out var ip) && ip.GetBoolean();
        OnPropertyChanged(nameof(PlayPauseText));

        if (root.TryGetProperty("device", out var dev) &&
            dev.TryGetProperty("volume_percent", out var vp) &&
            vp.ValueKind == JsonValueKind.Number)
        {
            VolumePercent = vp.GetInt32();
        }

        if (root.TryGetProperty("item", out var item))
        {
            Track = item.TryGetProperty("name", out var tn) ? tn.GetString() ?? "-" : "-";

            if (item.TryGetProperty("artists", out var artists) &&
                artists.ValueKind == JsonValueKind.Array &&
                artists.GetArrayLength() > 0)
            {
                var a0 = artists[0];
                Artist = a0.TryGetProperty("name", out var an) ? an.GetString() ?? "-" : "-";
            }
        }

        Status = "Connected";
        OnPropertyChanged(nameof(ConnectText));
    }

    private void TogglePin()
    {
        _isPinned = !_isPinned;
        OnPropertyChanged(nameof(PinText));

#if WINDOWS
        var win = Application.Current?.Windows.LastOrDefault(); // mini window is typically last opened
        win?.SetAlwaysOnTopWindows(_isPinned);
#endif
    }

    private void MinimizeWindow()
    {
#if WINDOWS
        var win = Application.Current?.Windows.LastOrDefault();
        win?.MinimizeWindows();
#endif
    }
}
