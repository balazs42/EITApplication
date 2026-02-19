using Utility.Classes.Spotify;

public sealed class SpotifySession
{
    private readonly ISpotifyTokenStore _store;
    private readonly SpotifyPkceLoopbackAuth _auth;
    private SpotifyTokenSet? _tokens;

    public SpotifySession(ISpotifyTokenStore store, SpotifyPkceLoopbackAuth auth)
    {
        _store = store;
        _auth = auth;
    }

    public async Task<bool> TryRestoreAsync(CancellationToken ct = default)
    {
        _tokens = await _store.GetAsync();
        if (_tokens is null) return false;

        if (DateTimeOffset.UtcNow >= _tokens.ExpiresAtUtc)
        {
            _tokens = await _auth.RefreshAsync(_tokens.RefreshToken, ct);
            await _store.SaveAsync(_tokens);
        }
        return true;
    }

    public async Task ConnectInteractiveAsync(CancellationToken ct = default)
    {
        _tokens = await _auth.LoginAsync(ct);
        await _store.SaveAsync(_tokens);
    }

    public async Task<string> GetValidAccessTokenAsync(CancellationToken ct = default)
    {
        if (_tokens is null)
        {
            if (!await TryRestoreAsync(ct))
                throw new InvalidOperationException("Not connected. User must connect Spotify.");
        }

        if (DateTimeOffset.UtcNow >= _tokens!.ExpiresAtUtc)
        {
            _tokens = await _auth.RefreshAsync(_tokens.RefreshToken, ct);
            await _store.SaveAsync(_tokens);
        }

        return _tokens!.AccessToken;
    }

    public async Task ClearAsync()
    {
        _tokens = null;
        await _store.ClearAsync();
    }
}
