using System.Diagnostics;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Utility.Classes.Spotify;

public sealed class SpotifyPkceLoopbackAuth
{
    private const string ClientId = "4405a5efe1f24f3586444e1a1eb663d0";

    // Must match Spotify dashboard exactly:
    private const string RedirectUri = "http://127.0.0.1:43811/callback/";

    private static readonly string[] Scopes =
    {
        "user-read-playback-state",
        "user-read-currently-playing",
        "user-modify-playback-state",
    };

    public async Task<SpotifyTokenSet> LoginAsync(CancellationToken ct = default)
    {
        var state = Guid.NewGuid().ToString("N");
        var verifier = Base64Url(RandomNumberGenerator.GetBytes(64));
        var challenge = Base64Url(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)));

        var scope = Uri.EscapeDataString(string.Join(' ', Scopes));
        var authUrl =
            "https://accounts.spotify.com/authorize" +
            $"?client_id={ClientId}" +
            $"&response_type=code" +
            $"&redirect_uri={Uri.EscapeDataString(RedirectUri)}" +
            $"&code_challenge_method=S256" +
            $"&code_challenge={challenge}" +
            $"&scope={scope}" +
            $"&state={state}";

        using var listener = new HttpListener();
        listener.Prefixes.Add(RedirectUri); // note: must end with '/' for HttpListener prefix
        listener.Start();

        // Open default browser
        Process.Start(new ProcessStartInfo(authUrl) { UseShellExecute = true });

        // Wait for the redirect
        var ctx = await listener.GetContextAsync().WaitAsync(ct);

        var query = ctx.Request.QueryString;
        var returnedState = query["state"];
        var code = query["code"];
        var error = query["error"];

        // Respond to browser quickly
        var html = "<html><body>You can close this tab and return to the app.</body></html>";
        var buffer = Encoding.UTF8.GetBytes(html);
        ctx.Response.ContentType = "text/html";
        ctx.Response.ContentLength64 = buffer.Length;
        await ctx.Response.OutputStream.WriteAsync(buffer, ct);
        ctx.Response.Close();

        if (!string.IsNullOrWhiteSpace(error))
            throw new InvalidOperationException($"Spotify auth error: {error}");

        if (returnedState != state)
            throw new InvalidOperationException("State mismatch (possible CSRF).");

        if (string.IsNullOrWhiteSpace(code))
            throw new InvalidOperationException("No authorization code returned.");

        // Exchange code for tokens (PKCE)
        using var http = new HttpClient();
        var tokenResp = await http.PostAsync(
            "https://accounts.spotify.com/api/token",
            new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["client_id"] = ClientId,
                ["grant_type"] = "authorization_code",
                ["code"] = code!,
                ["redirect_uri"] = RedirectUri,
                ["code_verifier"] = verifier,
            }),
            ct);

        var json = await tokenResp.Content.ReadAsStringAsync(ct);
        if (!tokenResp.IsSuccessStatusCode)
            throw new HttpRequestException($"Token exchange failed: {(int)tokenResp.StatusCode} {json}");

        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        var access = root.GetProperty("access_token").GetString()!;
        var refresh = root.GetProperty("refresh_token").GetString()!;
        var expiresIn = root.GetProperty("expires_in").GetInt32();

        return new SpotifyTokenSet
        {
            AccessToken = access,
            RefreshToken = refresh,
            ExpiresAtUtc = DateTimeOffset.UtcNow.AddSeconds(expiresIn - 30),
        };
    }

    public async Task<SpotifyTokenSet> RefreshAsync(string refreshToken, CancellationToken ct = default)
    {
        using var http = new HttpClient();
        var resp = await http.PostAsync(
            "https://accounts.spotify.com/api/token",
            new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["client_id"] = ClientId,
                ["grant_type"] = "refresh_token",
                ["refresh_token"] = refreshToken,
            }),
            ct);

        var json = await resp.Content.ReadAsStringAsync(ct);
        if (!resp.IsSuccessStatusCode)
            throw new HttpRequestException($"Refresh failed: {(int)resp.StatusCode} {json}");

        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        var access = root.GetProperty("access_token").GetString()!;
        var expiresIn = root.GetProperty("expires_in").GetInt32();

        // refresh_token may be omitted in refresh responses; keep old
        var newRefresh = root.TryGetProperty("refresh_token", out var rt) && rt.ValueKind == JsonValueKind.String
            ? rt.GetString()!
            : refreshToken;

        return new SpotifyTokenSet
        {
            AccessToken = access,
            RefreshToken = newRefresh,
            ExpiresAtUtc = DateTimeOffset.UtcNow.AddSeconds(expiresIn - 30),
        };
    }

    private static string Base64Url(byte[] data)
        => Convert.ToBase64String(data).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}
