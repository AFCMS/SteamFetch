public sealed class SteamKitInterface : IDisposable
{
    private SteamKit2.SteamClient? steamClient;
    private SteamKit2.CallbackManager? manager;
    private SteamKit2.SteamUser? steamUser;
    private SteamKit2.SteamApps? steamApps;

    private static readonly HttpClient httpClient = new();

    // Background callback loop management
    private CancellationTokenSource? _bgCts;
    private Task? _bgTask;
    private readonly object _lock = new();

    // Connection/login state
    private TaskCompletionSource<bool>? _loggedOnTcs;
    private volatile bool _isConnected;

    // KV cache per app
    private readonly Dictionary<uint, SteamKit2.KeyValue> _productInfoCache = new();

    // Pending request coordination
    private sealed class RequestState
    {
        public HashSet<uint> Pending { get; }
        public TaskCompletionSource<bool> Tcs { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public RequestState(IEnumerable<uint> ids)
        {
            Pending = new HashSet<uint>(ids);
        }
    }
    private readonly List<RequestState> _pendingRequests = new();

    public record ArtworkVariant(string Type, string Variant, string Language, string Url);

    // Optional convenience for backward compatibility: fetch default library capsule (image2x)
    public async Task<string?> FetchSingleArtwork(uint appId, string lang = "english", CancellationToken ct = default)
        => await FetchArtworkUrl(appId, "library_capsule", "image2x", lang, ct).ConfigureAwait(false);

    public static async Task DownloadAsync(string url, string outputFile, CancellationToken ct = default)
    {
        using var resp = await httpClient.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        resp.EnsureSuccessStatusCode();

        var dir = Path.GetDirectoryName(outputFile);
        if (!string.IsNullOrEmpty(dir))
        {
            Directory.CreateDirectory(dir);
        }

        await using var fs = File.Create(outputFile);
        await using var stream = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        await stream.CopyToAsync(fs, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Returns the raw KeyValue product info for an app (from cache or Steam if needed).
    /// </summary>
    public async Task<SteamKit2.KeyValue?> GetProductInfoKV(uint appId, CancellationToken ct = default)
    {
        await EnsureConnectedAsync(ct).ConfigureAwait(false);
        // Fast path from cache
        lock (_lock)
        {
            if (_productInfoCache.TryGetValue(appId, out var kv))
                return kv;
        }
        // Request and return
        await RequestProductInfoAsync(new[] { appId }, ct).ConfigureAwait(false);
        lock (_lock)
        {
            _productInfoCache.TryGetValue(appId, out var kv);
            return kv;
        }
    }

    private static SteamKit2.KeyValue? ChildCI(SteamKit2.KeyValue parent, string name)
        => parent.Children.FirstOrDefault(k => string.Equals(k.Name, name, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Fetch a specific artwork URL by type/variant/language.
    /// </summary>
    public async Task<string?> FetchArtworkUrl(uint appId, string type, string variant, string lang, CancellationToken ct = default)
    {
        var kv = await GetProductInfoKV(appId, ct).ConfigureAwait(false);
        if (kv is null)
            return null;

        var assets = kv["common"]?["library_assets_full"];
        if (assets is null)
            return null;

        // Case-insensitive navigation
        var typeKv = ChildCI(assets, type);
        if (typeKv is null)
            return null;
        var variantKv = ChildCI(typeKv, variant);
        if (variantKv is null)
            return null;
        var langKv = ChildCI(variantKv, lang);
        var rel = langKv?.Value;
        if (string.IsNullOrEmpty(rel))
            return null;

        return BuildAssetUrl(appId, rel);
    }

    /// <summary>
    /// Lists available artwork variants and languages for the app.
    /// </summary>
    public async Task<IReadOnlyList<ArtworkVariant>> ListAvailableArtworks(uint appId, CancellationToken ct = default)
    {
        var kv = await GetProductInfoKV(appId, ct).ConfigureAwait(false);
        var list = new List<ArtworkVariant>();
        if (kv is null)
            return list;

        var assets = kv["common"]?["library_assets_full"];
        if (assets is null)
            return list;

        // Iterate over asset types (e.g., library_capsule, library_hero, ...)
        foreach (var typeKv in assets.Children)
        {
            var typeName = typeKv.Name ?? "unknown";
            // Commonly, images have variants like image2x (retina). Collect languages under those.
            foreach (var variantKv in typeKv.Children)
            {
                var variantName = variantKv.Name ?? "unknown"; // e.g., image2x
                // Filter out non-image metadata like logo_position entries under library_logo
                if (string.Equals(typeName, "library_logo", StringComparison.OrdinalIgnoreCase) &&
                    string.Equals(variantName, "logo_position", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }
                // Languages are leaf nodes with values (relative paths)
                foreach (var langKv in variantKv.Children)
                {
                    var lang = langKv.Name ?? "unknown";
                    var rel = langKv.Value;
                    if (!string.IsNullOrEmpty(rel))
                    {
                        var url = BuildAssetUrl(appId, rel);
                        list.Add(new ArtworkVariant(typeName, variantName, lang, url));
                    }
                }
            }
        }

        return list;
    }

    public static string BuildAssetUrl(uint appId, string relativePath)
        => $"https://shared.fastly.steamstatic.com/store_item_assets/steam/apps/{appId}/{relativePath}";

    private async Task EnsureConnectedAsync(CancellationToken ct)
    {
        if (_isConnected && _loggedOnTcs is { Task.IsCompleted: true })
            return;

        if (steamClient is null)
        {
            steamClient = new SteamKit2.SteamClient();
            manager = new SteamKit2.CallbackManager(steamClient);
            steamUser = steamClient.GetHandler<SteamKit2.SteamUser>();
            steamApps = steamClient.GetHandler<SteamKit2.SteamApps>();

            // Subscriptions
            manager.Subscribe<SteamKit2.SteamClient.ConnectedCallback>(_ =>
            {
                steamUser!.LogOnAnonymous();
            });

            manager.Subscribe<SteamKit2.SteamClient.DisconnectedCallback>(cb =>
            {
                _isConnected = false;
                if (_loggedOnTcs is { Task.IsCompleted: false })
                {
                    _loggedOnTcs!.TrySetException(new Exception("Disconnected before login"));
                }
            });

            manager.Subscribe<SteamKit2.SteamUser.LoggedOnCallback>(cb =>
            {
                if (cb.Result == SteamKit2.EResult.OK)
                {
                    _loggedOnTcs?.TrySetResult(true);
                }
                else
                {
                    _loggedOnTcs?.TrySetException(new Exception($"Failed to log in: {cb.Result}"));
                }
            });

            manager.Subscribe<SteamKit2.SteamApps.PICSProductInfoCallback>(cb =>
            {
                lock (_lock)
                {
                    foreach (var kvp in cb.Apps)
                    {
                        var appId = kvp.Key;
                        var appKv = kvp.Value.KeyValues;
                        _productInfoCache[appId] = appKv;
                        // Notify all pending requests
                        foreach (var req in _pendingRequests)
                        {
                            req.Pending.Remove(appId);
                        }
                    }
                    // Complete any requests that have finished
                    for (int i = _pendingRequests.Count - 1; i >= 0; i--)
                    {
                        if (_pendingRequests[i].Pending.Count == 0)
                        {
                            _pendingRequests[i].Tcs.TrySetResult(true);
                            _pendingRequests.RemoveAt(i);
                        }
                    }
                }
            });
        }

        _loggedOnTcs = new(TaskCreationOptions.RunContinuationsAsynchronously);

        if (!_isConnected)
        {
            steamClient!.Connect();
            _isConnected = true;
        }

        if (_bgCts is null)
        {
            _bgCts = new CancellationTokenSource();
            var token = _bgCts.Token;
            _bgTask = Task.Run(async () =>
            {
                while (!token.IsCancellationRequested)
                {
                    try
                    {
                        manager!.RunWaitCallbacks(TimeSpan.FromMilliseconds(250));
                        await Task.Delay(100, token).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException)
                    {
                        break;
                    }
                    catch
                    {
                        // Swallow to keep background loop alive
                    }
                }
            }, token);
        }

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(TimeSpan.FromSeconds(30));
        await _loggedOnTcs.Task.WaitAsync(timeoutCts.Token).ConfigureAwait(false);
    }

    private async Task RequestProductInfoAsync(IReadOnlyCollection<uint> appIds, CancellationToken ct)
    {
        await EnsureConnectedAsync(ct).ConfigureAwait(false);

        // Determine which appIds are missing from cache
        List<uint> missing;
        lock (_lock)
        {
            missing = appIds.Where(id => !_productInfoCache.ContainsKey(id)).ToList();
        }
        if (missing.Count == 0)
            return;

        var reqState = new RequestState(missing);
        lock (_lock)
        {
            _pendingRequests.Add(reqState);
        }

        // Issue product info requests
        foreach (var id in missing)
        {
            _ = steamApps!.PICSGetProductInfo(new SteamKit2.SteamApps.PICSRequest(id), null, false);
        }

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(TimeSpan.FromSeconds(60));
        await reqState.Tcs.Task.WaitAsync(timeoutCts.Token).ConfigureAwait(false);
    }

    /// <summary>
    /// Prefetch product info for multiple app IDs in as few requests as possible.
    /// Subsequent calls to GetProductInfoKV/FetchArtworkUrl will hit the cache.
    /// </summary>
    public async Task PrefetchProductInfos(IEnumerable<uint> appIds, CancellationToken ct = default)
    {
        var ids = appIds.Distinct().ToArray();
        if (ids.Length == 0)
            return;
        await RequestProductInfoAsync(ids, ct).ConfigureAwait(false);
    }

    public void Dispose()
    {
        try
        {
            _bgCts?.Cancel();
            _bgTask?.Wait(TimeSpan.FromSeconds(2));
        }
        catch { }
        finally
        {
            _bgCts?.Dispose();
            _bgCts = null;
            _bgTask = null;
        }

        if (_isConnected)
        {
            try { steamClient?.Disconnect(); } catch { }
        }
        _isConnected = false;
    }
}