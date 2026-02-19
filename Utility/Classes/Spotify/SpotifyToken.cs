using System.Text.Json;

namespace Utility.Classes.Spotify
{
    public sealed class SpotifyTokenSet
    {
        public string AccessToken { get; init; } = "";
        public string RefreshToken { get; init; } = "";
        public DateTimeOffset ExpiresAtUtc { get; init; }
    }

    public interface ISpotifyTokenStore
    {
        Task<SpotifyTokenSet?> GetAsync();
        Task SaveAsync(SpotifyTokenSet tokens);
        Task ClearAsync();
    }

    public sealed class SpotifyTokenStore : ISpotifyTokenStore
    {
        private const string Key = "spotify_tokens_v1";

        public async Task<SpotifyTokenSet?> GetAsync()
        {
            var json = await Microsoft.Maui.Storage.SecureStorage.Default.GetAsync(Key);
            return string.IsNullOrWhiteSpace(json) ? null : JsonSerializer.Deserialize<SpotifyTokenSet>(json);
        }

        public Task SaveAsync(SpotifyTokenSet tokens)
            => Microsoft.Maui.Storage.SecureStorage.Default.SetAsync(Key, JsonSerializer.Serialize(tokens));

        public Task ClearAsync()
        {
            Microsoft.Maui.Storage.SecureStorage.Default.Remove(Key);
            return Task.CompletedTask;
        }
    }
}
