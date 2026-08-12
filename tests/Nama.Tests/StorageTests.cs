using System.Net;
using Nama.Core.Abstractions;
using Nama.Core.Aggregation;
using Nama.Core.Models;
using Nama.Providers;
using Nama.Storage;

namespace Nama.Tests;

public class NamaSettingsTests : IDisposable
{
    private readonly string _path = Path.Combine(
        Path.GetTempPath(), $"nama-settings-{Guid.NewGuid():N}.json");

    public void Dispose()
    {
        if (File.Exists(_path)) File.Delete(_path);
    }

    [Fact]
    public void Api_key_round_trips_through_encryption()
    {
        new NamaSettings { SteamGridDbApiKey = "secret-key-value" }.Save(_path);

        Assert.Equal("secret-key-value", NamaSettings.Load(_path).SteamGridDbApiKey);
    }

    [Fact]
    public void Api_key_is_never_written_in_plaintext()
    {
        new NamaSettings { SteamGridDbApiKey = "secret-key-value" }.Save(_path);

        var onDisk = File.ReadAllText(_path);

        Assert.DoesNotContain("secret-key-value", onDisk);
        Assert.Contains("steamGridDbApiKeyProtected", onDisk);
    }

    [Fact]
    public void Other_settings_round_trip()
    {
        new NamaSettings
        {
            PreferredSteamAccountId = 123456789,
            ContextMenuInstalled = true,
            AlwaysCloseSteam = true,
            ExperimentalDlsiteEnabled = false,
            ExperimentalVndbEnabled = false,
        }.Save(_path);

        var loaded = NamaSettings.Load(_path);

        Assert.Equal(123456789u, loaded.PreferredSteamAccountId);
        Assert.True(loaded.ContextMenuInstalled);
        Assert.True(loaded.AlwaysCloseSteam);
        Assert.False(loaded.ExperimentalDlsiteEnabled);
        Assert.False(loaded.ExperimentalVndbEnabled);
    }

    [Fact]
    public void Clearing_the_key_removes_the_stored_value()
    {
        var settings = new NamaSettings { SteamGridDbApiKey = "abc" };
        settings.SteamGridDbApiKey = null;

        Assert.Null(settings.SteamGridDbApiKeyProtected);
    }

    [Fact]
    public void Missing_file_yields_defaults()
    {
        var loaded = NamaSettings.Load(Path.Combine(Path.GetTempPath(), "nama-absent.json"));

        Assert.Null(loaded.SteamGridDbApiKey);
        Assert.Null(loaded.PreferredSteamAccountId);
        Assert.True(loaded.ExperimentalDlsiteEnabled);
        Assert.True(loaded.ExperimentalVndbEnabled);
    }

    [Fact]
    public void Corrupt_file_yields_defaults_rather_than_throwing()
    {
        File.WriteAllText(_path, "{ this is not json");

        Assert.Null(NamaSettings.Load(_path).SteamGridDbApiKey);
    }

    [Fact]
    public void Corrupt_ciphertext_is_treated_as_no_key()
    {
        // What a settings file copied from another machine or user account looks like.
        File.WriteAllText(_path, """{"steamGridDbApiKeyProtected":"bm90LWEtcmVhbC1kcGFwaS1ibG9i"}""");

        Assert.Null(NamaSettings.Load(_path).SteamGridDbApiKey);
    }

    [Fact]
    public void Saving_twice_overwrites_cleanly()
    {
        new NamaSettings { SteamGridDbApiKey = "first" }.Save(_path);
        new NamaSettings { SteamGridDbApiKey = "second" }.Save(_path);

        Assert.Equal("second", NamaSettings.Load(_path).SteamGridDbApiKey);
        Assert.False(File.Exists(_path + ".tmp"));
    }
}

public class SqliteSearchCacheTests : IDisposable
{
    private readonly string _path = Path.Combine(
        Path.GetTempPath(), $"nama-cache-{Guid.NewGuid():N}", "responses.db");

    private readonly SqliteSearchCache _cache;

    public SqliteSearchCacheTests() => _cache = new SqliteSearchCache(_path);

    public void Dispose()
    {
        _cache.Dispose();

        var directory = Path.GetDirectoryName(_path)!;
        if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
    }

    [Fact]
    public async Task Stores_and_retrieves_a_payload()
    {
        await _cache.SetAsync("k", "payload", TimeSpan.FromMinutes(5));

        Assert.Equal("payload", await _cache.GetAsync("k"));
    }

    [Fact]
    public async Task Missing_key_returns_null()
    {
        Assert.Null(await _cache.GetAsync("absent"));
    }

    [Fact]
    public async Task Expired_entries_are_not_returned()
    {
        await _cache.SetAsync("k", "payload", TimeSpan.FromSeconds(-1));

        Assert.Null(await _cache.GetAsync("k"));
    }

    [Fact]
    public async Task Writing_the_same_key_replaces_it()
    {
        await _cache.SetAsync("k", "first", TimeSpan.FromMinutes(5));
        await _cache.SetAsync("k", "second", TimeSpan.FromMinutes(5));

        Assert.Equal("second", await _cache.GetAsync("k"));
        Assert.Equal(1, _cache.Count());
    }

    [Fact]
    public async Task Clear_empties_the_cache()
    {
        await _cache.SetAsync("a", "1", TimeSpan.FromMinutes(5));
        await _cache.SetAsync("b", "2", TimeSpan.FromMinutes(5));

        await _cache.ClearAsync();

        Assert.Equal(0, _cache.Count());
        Assert.Null(await _cache.GetAsync("a"));
    }

    [Fact]
    public async Task Handles_large_payloads_and_unicode_keys()
    {
        var payload = new string('x', 500_000);
        await _cache.SetAsync("POST https://api.vndb.org 素晴らしき日々", payload, TimeSpan.FromMinutes(5));

        Assert.Equal(payload, await _cache.GetAsync("POST https://api.vndb.org 素晴らしき日々"));
    }
}

public class ProviderHttpCachingTests
{
    private sealed class MemoryCache : ISearchCache
    {
        private readonly Dictionary<string, string> _entries = [];

        public int Writes { get; private set; }

        public Task<string?> GetAsync(string key, CancellationToken ct = default) =>
            Task.FromResult(_entries.GetValueOrDefault(key));

        public Task SetAsync(string key, string payload, TimeSpan ttl, CancellationToken ct = default)
        {
            _entries[key] = payload;
            Writes++;
            return Task.CompletedTask;
        }

        public Task ClearAsync(CancellationToken ct = default)
        {
            _entries.Clear();
            return Task.CompletedTask;
        }
    }

    [Fact]
    public async Task A_repeated_request_is_served_from_cache()
    {
        var handler = new StubHandler(_ => StubHandler.Json("""{"ok":true}"""));
        var http = new ProviderHttp(StubHandler.Client(handler), new ProviderOptions(), new MemoryCache());

        (await http.GetJsonAsync("https://example.test/a"))?.Dispose();
        (await http.GetJsonAsync("https://example.test/a"))?.Dispose();

        Assert.Equal(1, handler.CallCount);
    }

    [Fact]
    public async Task Failures_are_not_cached()
    {
        var cache = new MemoryCache();
        var handler = new StubHandler(_ => StubHandler.Status(HttpStatusCode.TooManyRequests));
        var http = new ProviderHttp(StubHandler.Client(handler), new ProviderOptions(), cache);

        Assert.Null(await http.GetJsonAsync("https://example.test/a"));
        Assert.Null(await http.GetJsonAsync("https://example.test/a"));

        // A rate limit must not be remembered for a week.
        Assert.Equal(0, cache.Writes);
        Assert.Equal(2, handler.CallCount);
    }

    [Fact]
    public async Task Rate_limiting_is_reported_clearly()
    {
        var handler = new StubHandler(_ => StubHandler.Status(HttpStatusCode.TooManyRequests));
        var http = new ProviderHttp(StubHandler.Client(handler), new ProviderOptions());

        await http.GetJsonAsync("https://example.test/a");

        Assert.Contains("rate limited", http.LastError);
    }

    [Fact]
    public async Task A_bad_key_is_reported_clearly()
    {
        var handler = new StubHandler(_ => StubHandler.Status(HttpStatusCode.Unauthorized));
        var http = new ProviderHttp(StubHandler.Client(handler), new ProviderOptions());

        await http.GetJsonAsync("https://example.test/a");

        Assert.Contains("API key", http.LastError);
    }

    [Fact]
    public async Task Malformed_json_yields_null_rather_than_throwing()
    {
        var handler = new StubHandler(_ => StubHandler.Json("not json at all"));
        var http = new ProviderHttp(StubHandler.Client(handler), new ProviderOptions());

        Assert.Null(await http.GetJsonAsync("https://example.test/a"));
    }

    [Fact]
    public async Task Post_bodies_are_part_of_the_cache_key()
    {
        var handler = new StubHandler(_ => StubHandler.Json("""{"ok":true}"""));
        var http = new ProviderHttp(StubHandler.Client(handler), new ProviderOptions(), new MemoryCache());

        (await http.PostJsonAsync("https://example.test/q", """{"filters":["search","=","a"]}"""))?.Dispose();
        (await http.PostJsonAsync("https://example.test/q", """{"filters":["search","=","b"]}"""))?.Dispose();

        // Same URL, different query — these must not collide.
        Assert.Equal(2, handler.CallCount);
    }
}

public class AggregatorTests
{
    private sealed class ThrowingArtworkProvider : IArtworkProvider
    {
        public string SourceId => "boom";

        public string DisplayName => "Boom";

        public bool IsAvailable => true;

        public IReadOnlyCollection<ArtworkType> SupportedTypes { get; } = [ArtworkType.Cover];

        public bool CanResolve(GameRef game) => true;

        public Task<IReadOnlyList<Artwork>> GetArtworkAsync(GameRef game, CancellationToken ct = default) =>
            throw new InvalidOperationException("provider bug");
    }

    private sealed class StaticArtworkProvider : IArtworkProvider
    {
        public string SourceId => "static";

        public string DisplayName => "Static";

        public bool IsAvailable => true;

        public IReadOnlyCollection<ArtworkType> SupportedTypes { get; } = [ArtworkType.Cover];

        public bool CanResolve(GameRef game) => true;

        public Task<IReadOnlyList<Artwork>> GetArtworkAsync(GameRef game, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<Artwork>>(
            [
                new Artwork { Id = "1", Type = ArtworkType.Cover, Url = "u", Source = "static", Width = 600, Height = 900 },
            ]);
    }

    [Fact]
    public async Task One_provider_throwing_does_not_lose_the_others()
    {
        var aggregator = new ArtworkAggregator([new ThrowingArtworkProvider(), new StaticArtworkProvider()]);

        var collection = await aggregator.GetArtworkAsync(new GameRef([], "x"));

        Assert.Single(collection.All);
        Assert.Contains("Boom", collection.FailedProviders);
    }

    [Fact]
    public async Task Unavailable_providers_are_reported_as_skipped_not_failed()
    {
        var aggregator = new ArtworkAggregator([new Providers.Igdb.IgdbProvider(), new StaticArtworkProvider()]);

        var collection = await aggregator.GetArtworkAsync(new GameRef([], "x"));

        Assert.Contains("IGDB", collection.SkippedProviders);
        Assert.Empty(collection.FailedProviders);
    }
}
