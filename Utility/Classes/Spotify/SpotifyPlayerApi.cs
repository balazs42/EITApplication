using System.Net;
using System.Text;
using System.Text.Json;

namespace Utility.Classes.Spotify;
public sealed record SpotifyDevice(string Id, string Name, bool IsActive, string Type, int? VolumePercent);
public sealed class SpotifyPlayerApi
{
    private readonly SpotifySession _session;
    private readonly HttpClient _http = new() { BaseAddress = new Uri("https://api.spotify.com/v1/") };

    public SpotifyPlayerApi(SpotifySession session) => _session = session;

    private async Task AuthorizeAsync(CancellationToken ct)
    {
        var token = await _session.GetValidAccessTokenAsync(ct);
        _http.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);
    }

    public async Task<string?> GetPlaybackAsync(CancellationToken ct = default)
    {
        await AuthorizeAsync(ct);
        var resp = await _http.GetAsync("me/player", ct);
        if (resp.StatusCode == HttpStatusCode.NoContent) return null;
        var body = await resp.Content.ReadAsStringAsync(ct);
        if (!resp.IsSuccessStatusCode) throw new HttpRequestException($"{(int)resp.StatusCode} {body}");
        return body;
    }

   
    public async Task<List<SpotifyDevice>> GetDevicesParsedAsync(CancellationToken ct = default)
    {
        var json = await GetDevicesAsync(ct); // your existing method returns JSON string
        using var doc = JsonDocument.Parse(json);

        var list = new List<SpotifyDevice>();
        if (!doc.RootElement.TryGetProperty("devices", out var devices) || devices.ValueKind != JsonValueKind.Array)
            return list;

        foreach (var d in devices.EnumerateArray())
        {
            var id = d.GetProperty("id").GetString() ?? "";
            if (string.IsNullOrWhiteSpace(id)) continue;

            var name = d.GetProperty("name").GetString() ?? "-";
            var isActive = d.TryGetProperty("is_active", out var ia) && ia.GetBoolean();
            var type = d.TryGetProperty("type", out var t) ? t.GetString() ?? "" : "";
            int? vol = d.TryGetProperty("volume_percent", out var vp) && vp.ValueKind == JsonValueKind.Number
                ? vp.GetInt32()
                : null;

            list.Add(new SpotifyDevice(id, name, isActive, type, vol));
        }
        return list;
    }

    public async Task<string?> EnsureActiveDeviceAsync(CancellationToken ct = default)
    {
        var devices = await GetDevicesParsedAsync(ct);

        // Prefer already active device; otherwise take first available
        var target = devices.FirstOrDefault(d => d.IsActive) ?? devices.FirstOrDefault();
        if (target is null) return null;

        // Transfer playback to make it the active target
        await TransferPlaybackAsync(target.Id, play: false, ct); // your existing method
        return target.Id;
    }

    public async Task PlaySafeAsync(CancellationToken ct = default)
    {
        try
        {
            await PlayAsync(ct); // your existing PUT me/player/play
        }
        catch (HttpRequestException ex) when (ex.Message.Contains("NO_ACTIVE_DEVICE", StringComparison.OrdinalIgnoreCase) ||
                                             ex.Message.Contains("No active device", StringComparison.OrdinalIgnoreCase))
        {
            var deviceId = await EnsureActiveDeviceAsync(ct);
            if (deviceId is null)
                throw new InvalidOperationException(
                    "No Spotify Connect device is available. Open Spotify Desktop/Web/Phone once, then try again.");

            // Now target it explicitly
            await PutNoBody($"me/player/play?device_id={Uri.EscapeDataString(deviceId)}", ct);
        }
    }

    public Task PlayAsync(CancellationToken ct = default) => PutNoBody("me/player/play", ct);
    public Task PauseAsync(CancellationToken ct = default) => PutNoBody("me/player/pause", ct);
    public Task NextAsync(CancellationToken ct = default) => PostNoBody("me/player/next", ct);
    public Task PreviousAsync(CancellationToken ct = default) => PostNoBody("me/player/previous", ct);

    public Task SetVolumeAsync(int volumePercent, CancellationToken ct = default)
    {
        volumePercent = Math.Clamp(volumePercent, 0, 100);
        return PutNoBody($"me/player/volume?volume_percent={volumePercent}", ct);
    }

    public async Task<string> GetDevicesAsync(CancellationToken ct = default)
    {
        await AuthorizeAsync(ct);
        var resp = await _http.GetAsync("me/player/devices", ct);
        var body = await resp.Content.ReadAsStringAsync(ct);
        if (!resp.IsSuccessStatusCode) throw new HttpRequestException($"{(int)resp.StatusCode} {body}");
        return body;
    }

    public async Task TransferPlaybackAsync(string deviceId, bool play = true, CancellationToken ct = default)
    {
        await AuthorizeAsync(ct);
        var payload = JsonSerializer.Serialize(new { device_ids = new[] { deviceId }, play });
        using var req = new HttpRequestMessage(HttpMethod.Put, "me/player")
        {
            Content = new StringContent(payload, Encoding.UTF8, "application/json")
        };
        var resp = await _http.SendAsync(req, ct);
        var body = await resp.Content.ReadAsStringAsync(ct);
        if (!resp.IsSuccessStatusCode) throw new HttpRequestException($"{(int)resp.StatusCode} {body}");
    }

    private async Task PutNoBody(string path, CancellationToken ct)
    {
        await AuthorizeAsync(ct);
        using var req = new HttpRequestMessage(HttpMethod.Put, path);
        var resp = await _http.SendAsync(req, ct);
        var body = await resp.Content.ReadAsStringAsync(ct);
        if (!resp.IsSuccessStatusCode) throw new HttpRequestException($"{(int)resp.StatusCode} {body}");
    }

    private async Task PostNoBody(string path, CancellationToken ct)
    {
        await AuthorizeAsync(ct);
        using var req = new HttpRequestMessage(HttpMethod.Post, path);
        var resp = await _http.SendAsync(req, ct);
        var body = await resp.Content.ReadAsStringAsync(ct);
        if (!resp.IsSuccessStatusCode) throw new HttpRequestException($"{(int)resp.StatusCode} {body}");
    }
}
