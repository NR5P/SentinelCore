using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.Options;

namespace SentinelCore;

// Makes picking a vision model in Settings enough on its own: if Ollama:Model names a model that
// isn't pulled yet, this pulls it. Until the pull finishes, alerts keep going to the model that was
// working before the change (ActiveModel), so switching models never leaves a gap where every
// alert fails with Ollama's 404 - the switch happens the moment the download completes.
//
// Runs on startup, whenever the setting changes (appsettings.json is reloaded live), and every
// ten minutes after that, which also retries a pull that failed (Ollama down, network blip).
//
// Only while VisionRouting can actually send something to Ollama (see OllamaInUse). A fresh
// install starts on the fast local detector and never needs the multi-gigabyte model, so it
// isn't downloaded until someone picks Ollama - in the setup wizard or in Settings - at which
// point the routing change wakes this up and the pull starts.
public sealed class OllamaModelProvisioner : BackgroundService
{
    private static readonly TimeSpan RecheckInterval = TimeSpan.FromMinutes(10);

    private readonly HttpClient _httpClient;
    private readonly IOptionsMonitor<OllamaOptions> _options;
    private readonly IOptionsMonitor<VisionRoutingOptions>? _routing;
    private readonly IOptionsMonitor<CloudVisionOptions>? _cloud;
    private readonly ILogger<OllamaModelProvisioner> _logger;
    private readonly SemaphoreSlim _wake = new(0);
    private readonly Lock _lock = new();

    private string? _activeModel;
    private OllamaModelDownload? _download;

    // The warm-up load - see EnsureLoadedAsync. Its own token, not any caller's, so nothing but
    // shutting down can cancel it.
    private readonly CancellationTokenSource _stopping = new();
    private Task? _warmUp;
    private string? _warmUpModel;
    private (string Model, DateTimeOffset At)? _confirmedLoaded;
    private static readonly TimeSpan LoadedRecheck = TimeSpan.FromSeconds(30);

    // routing/cloud null means "Ollama is always in use" - the behaviour before routing was
    // consulted, and what the tests that only exercise the pull itself want.
    public OllamaModelProvisioner(
        HttpClient httpClient,
        IOptionsMonitor<OllamaOptions> options,
        ILogger<OllamaModelProvisioner> logger,
        IOptionsMonitor<VisionRoutingOptions>? routing = null,
        IOptionsMonitor<CloudVisionOptions>? cloud = null)
    {
        _httpClient = httpClient;
        _options = options;
        _routing = routing;
        _cloud = cloud;
        _logger = logger;
        _options.OnChange(_ => _wake.Release());
        _routing?.OnChange(_ => _wake.Release());
        _cloud?.OnChange(_ => _wake.Release());
    }

    private bool InUse =>
        _routing is null || OllamaInUse(_routing.CurrentValue, _cloud?.CurrentValue ?? new CloudVisionOptions());

    // Whether FailoverVisionClient could route any alert to Ollama with these settings - the same
    // decisions ResolveOrder and the escalation target make, collapsed to a yes/no. Gate checks are
    // the one exception: on the fast detector without cloud they go to Ollama too, but a gate check
    // is a feature someone turns on, and its settings say it needs a full AI, so they don't count.
    public static bool OllamaInUse(VisionRoutingOptions routing, CloudVisionOptions cloud)
    {
        var cloudConfigured = cloud.Enabled && !string.IsNullOrWhiteSpace(cloud.ApiKey);
        return routing.Primary.ToLowerInvariant() switch
        {
            // Misconfigured cloud falls through to Ollama; a working one falls back to it.
            "cloud" => !cloudConfigured || routing.FallbackEnabled,
            // Fallback reaches Ollama directly without cloud, and for bypassed cameras with it.
            "fastlocal" => routing.FallbackEnabled
                           || (routing.EscalationEnabled && routing.EscalationTarget.ToLowerInvariant() switch
                           {
                               "ollama" => true,
                               "cloud" => false,
                               _ => !cloudConfigured
                           }),
            _ => true
        };
    }

    // The model alerts should be sent to right now: the configured one once it is installed, the
    // previously working one while the configured one downloads. Falls back to the configured name
    // before the first check has run, which is exactly what happened before this class existed.
    public string ModelForAlerts
    {
        get
        {
            lock (_lock)
                return _activeModel ?? _options.CurrentValue.Model;
        }
    }

    // True once a check has found the configured model absent with no previous model to use in the
    // meantime - i.e. it is downloading, or the download failed. Deliberately false before the first
    // check and when Ollama can't be reached at all: those are not "the model is missing", and an
    // existing install must not drop to the fast detector for the few seconds after every restart.
    public bool ModelMissing
    {
        get
        {
            lock (_lock)
                return _download is not null && _activeModel is null;
        }
    }

    public OllamaModelStatus Status
    {
        get
        {
            lock (_lock)
                return new OllamaModelStatus(_options.CurrentValue.Model, _activeModel, _download, InUse);
        }
    }

    // Whether the alert model is loaded into Ollama's memory, starting a load if it isn't and
    // waiting at most `wait` for it. True when it is ready, when Ollama couldn't be asked (the
    // alert's own request then fails or succeeds on its merits), or when the load itself failed;
    // false only while a load is still running.
    //
    // Why it exists: Ollama ties a model load to the request that started it, and abandons the
    // load when that request is cancelled. An alert's request gives up after RequestTimeoutSeconds
    // (300s), so on a box that needs longer than that to load the model - gemma4 took ~10 minutes
    // on an 8 GB test VM, 2026-11 - every alert cancelled the load it was waiting on, the next one
    // started it again from nothing, and the full AI never answered at all. The load here is a
    // separate request with no deadline, so it finishes however long it takes, and alerts in the
    // meantime go to quick detection (FailoverVisionClient) instead of each waiting 15 minutes.
    public async Task<bool> EnsureLoadedAsync(TimeSpan wait, CancellationToken cancellationToken)
    {
        var model = ModelForAlerts;
        lock (_lock)
        {
            if (_confirmedLoaded is { } c && IsSameModel(c.Model, model) && DateTimeOffset.UtcNow - c.At < LoadedRecheck)
                return true;
        }

        bool loaded;
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(5));
            loaded = await IsLoadedAsync(model, timeout.Token);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
        {
            if (cancellationToken.IsCancellationRequested) throw;
            return true;
        }

        if (loaded)
        {
            MarkLoaded(model);
            return true;
        }

        Task warmUp;
        lock (_lock)
        {
            if (_warmUp is null || _warmUp.IsCompleted || !IsSameModel(_warmUpModel, model))
            {
                _warmUpModel = model;
                _warmUp = WarmUpAsync(model, _stopping.Token);
            }
            warmUp = _warmUp;
        }

        var finished = await Task.WhenAny(warmUp, Task.Delay(wait, cancellationToken)) == warmUp;
        cancellationToken.ThrowIfCancellationRequested();
        return finished;
    }

    private async Task<bool> IsLoadedAsync(string model, CancellationToken ct)
    {
        using var response = await _httpClient.GetAsync("api/ps", ct);
        response.EnsureSuccessStatusCode();
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
        if (!document.RootElement.TryGetProperty("models", out var models) || models.ValueKind != JsonValueKind.Array)
            return false;
        foreach (var entry in models.EnumerateArray())
            foreach (var key in new[] { "name", "model" })
                if (entry.TryGetProperty(key, out var name) && IsSameModel(name.GetString(), model))
                    return true;
        return false;
    }

    private async Task WarmUpAsync(string model, CancellationToken ct)
    {
        var startedAt = DateTimeOffset.UtcNow;
        _logger.LogInformation("Loading Ollama model {Model} into memory; alerts use quick detection if it takes a while", model);
        try
        {
            // No prompt: Ollama loads the model and answers once it is in memory.
            using var response = await _httpClient.PostAsJsonAsync(
                "api/generate", new { model, keep_alive = _options.CurrentValue.KeepAlive }, ct);
            var body = await response.Content.ReadAsStringAsync(ct);
            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning("Ollama could not load model {Model}: {Status} {Body}", model, (int)response.StatusCode, body);
                return;
            }
            MarkLoaded(model);
            _logger.LogInformation("Ollama model {Model} loaded in {Seconds:n0}s", model, (DateTimeOffset.UtcNow - startedAt).TotalSeconds);
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            _logger.LogWarning(ex, "Loading Ollama model {Model} failed", model);
        }
        catch (OperationCanceledException)
        {
        }
    }

    private void MarkLoaded(string model)
    {
        lock (_lock)
            _confirmedLoaded = (model, DateTimeOffset.UtcNow);
    }

    public override Task StopAsync(CancellationToken cancellationToken)
    {
        _stopping.Cancel();
        return base.StopAsync(cancellationToken);
    }

    public override void Dispose()
    {
        _stopping.Cancel();
        base.Dispose();
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await EnsureConfiguredModelAsync(stoppingToken);
            }
            catch (Exception ex) when (!stoppingToken.IsCancellationRequested)
            {
                _logger.LogWarning(ex, "Could not check or pull the configured Ollama model");
            }

            try
            {
                await _wake.WaitAsync(RecheckInterval, stoppingToken);
                // A burst of file-change events from one save is one change.
                while (_wake.CurrentCount > 0) await _wake.WaitAsync(stoppingToken);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    private async Task EnsureConfiguredModelAsync(CancellationToken ct)
    {
        var wanted = _options.CurrentValue.Model?.Trim();
        if (string.IsNullOrWhiteSpace(wanted)) return;
        // Not even asking Ollama for its list: on a quick-detection install it may not be running,
        // and a warning every ten minutes about a server nothing uses is noise.
        if (!InUse) return;

        var installed = await InstalledModelsAsync(ct);
        if (installed.Any(name => IsSameModel(name, wanted)))
        {
            lock (_lock)
            {
                _activeModel = wanted;
                _download = null;
            }
            return;
        }

        // Keep alerts on the old model only if it is still there to use.
        lock (_lock)
        {
            if (_activeModel is not null && !installed.Any(name => IsSameModel(name, _activeModel)))
                _activeModel = null;
            _download = new OllamaModelDownload(wanted, 0, null);
        }

        _logger.LogInformation("Ollama model {Model} is not installed - downloading it now", wanted);
        var error = await PullAsync(wanted, ct);

        lock (_lock)
        {
            if (error is null)
            {
                _activeModel = wanted;
                _download = null;
            }
            else
            {
                _download = new OllamaModelDownload(wanted, _download?.Percent ?? 0, error);
            }
        }

        if (error is null)
            _logger.LogInformation("Downloaded Ollama model {Model}; alerts now use it", wanted);
        else
            _logger.LogWarning("Downloading Ollama model {Model} failed: {Error}", wanted, error);
    }

    private async Task<List<string>> InstalledModelsAsync(CancellationToken ct)
    {
        var tags = await _httpClient.GetFromJsonAsync<TagsResponse>("api/tags", ct);
        return [.. (tags?.Models ?? []).Select(m => m.Name).OfType<string>()];
    }

    // Ollama streams the pull as one JSON object per line. Returns null on success or the reason it
    // failed, in Ollama's own words where it gave some (e.g. "pull model manifest: file does not
    // exist" for a misspelled tag).
    private async Task<string?> PullAsync(string model, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "api/pull")
        {
            Content = JsonContent.Create(new { model, stream = true })
        };
        using var response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
        if (!response.IsSuccessStatusCode)
            return $"Ollama answered {(int)response.StatusCode}: {await response.Content.ReadAsStringAsync(ct)}";

        // Known up front only from the registry: the pull itself reports one layer at a time, and
        // gemma4:e2b-it-qat is two big layers (a ~1 GB vision projector, then ~3.3 GB of weights),
        // so a per-layer percentage ran to 98% and then started again from 0 - which reads as a
        // failed download. Without the manifest it falls back to the per-layer figure.
        var modelBytes = await ManifestTotalBytesAsync(model, ct);
        var layers = new Dictionary<string, long>();

        using var reader = new StreamReader(await response.Content.ReadAsStreamAsync(ct));
        string? lastStatus = null;
        while (await reader.ReadLineAsync(ct) is { } line)
        {
            var update = ParsePullLine(line);
            if (update is null) continue;
            if (update.Error is not null) return update.Error;
            lastStatus = update.Status;
            if (update.Percent is not { } percent) continue;
            if (modelBytes is > 0 && update.Digest is { } digest && update.Completed is { } done)
            {
                layers[digest] = done;
                percent = (int)Math.Clamp(layers.Values.Sum() * 100 / modelBytes.Value, 0, 100);
            }
            lock (_lock)
                _download = new OllamaModelDownload(model, percent, null);
        }

        return lastStatus == "success" ? null : "the download stopped before Ollama reported success";
    }

    // The whole model's size, from the same public registry Ollama pulls from. Null for anything
    // that isn't a plain registry.ollama.ai name (a host-qualified model, a typo) or when the
    // registry doesn't answer quickly - progress then falls back to per layer.
    private async Task<long?> ManifestTotalBytesAsync(string model, CancellationToken ct)
    {
        var url = ManifestUrl(model);
        if (url is null) return null;
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(10));
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.Accept.ParseAdd("application/vnd.docker.distribution.manifest.v2+json");
            using var response = await _httpClient.SendAsync(request, timeout.Token);
            if (!response.IsSuccessStatusCode) return null;
            return ManifestTotalBytes(await response.Content.ReadAsStringAsync(timeout.Token));
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
        {
            return null;
        }
    }

    // "gemma4:e2b-it-qat" -> library/gemma4, tag e2b-it-qat; "someone/model" -> someone/model:latest.
    public static Uri? ManifestUrl(string model)
    {
        var name = model.Trim();
        var tag = "latest";
        var colon = name.LastIndexOf(':');
        if (colon > name.LastIndexOf('/'))
        {
            tag = name[(colon + 1)..];
            name = name[..colon];
        }
        var parts = name.Split('/');
        if (parts.Length > 2 || parts.Any(p => p.Length == 0) || (parts.Length == 2 && parts[0].Contains('.')))
            return null;
        var repository = parts.Length == 1 ? $"library/{parts[0]}" : name;
        return new Uri($"https://registry.ollama.ai/v2/{repository}/manifests/{tag}");
    }

    public static long? ManifestTotalBytes(string manifestJson)
    {
        using var document = JsonDocument.Parse(manifestJson);
        if (!document.RootElement.TryGetProperty("layers", out var layers) || layers.ValueKind != JsonValueKind.Array)
            return null;
        long total = 0;
        foreach (var layer in layers.EnumerateArray())
            if (layer.TryGetProperty("size", out var size) && size.TryGetInt64(out var bytes))
                total += bytes;
        if (document.RootElement.TryGetProperty("config", out var config) &&
            config.TryGetProperty("size", out var configSize) && configSize.TryGetInt64(out var configBytes))
            total += configBytes;
        return total > 0 ? total : null;
    }

    // Public so the progress arithmetic can be tested against real pull lines without Ollama.
    // Percent here is the one layer's; PullAsync turns it into the whole model's when it can.
    public static PullUpdate? ParsePullLine(string line)
    {
        if (string.IsNullOrWhiteSpace(line)) return null;
        try
        {
            using var document = JsonDocument.Parse(line);
            var root = document.RootElement;
            if (root.TryGetProperty("error", out var error))
                return new PullUpdate(null, null, error.GetString() ?? "unknown error");

            var status = root.TryGetProperty("status", out var s) ? s.GetString() : null;
            int? percent = null;
            long? completedBytes = null;
            if (root.TryGetProperty("total", out var total) && total.TryGetInt64(out var totalBytes) && totalBytes > 0 &&
                root.TryGetProperty("completed", out var completed) && completed.TryGetInt64(out var done))
            {
                completedBytes = done;
                percent = (int)Math.Clamp(done * 100 / totalBytes, 0, 100);
            }
            var digest = root.TryGetProperty("digest", out var d) ? d.GetString() : null;
            return new PullUpdate(status, percent, null, digest, completedBytes);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    // Same rule as OllamaVisionClient: an untagged name means ":latest".
    public static bool IsSameModel(string? installed, string configured)
    {
        if (string.IsNullOrWhiteSpace(installed)) return false;
        if (installed.Equals(configured, StringComparison.OrdinalIgnoreCase)) return true;
        return !configured.Contains(':', StringComparison.Ordinal)
               && installed.Equals($"{configured}:latest", StringComparison.OrdinalIgnoreCase);
    }

    public sealed record PullUpdate(string? Status, int? Percent, string? Error, string? Digest = null, long? Completed = null);

    private sealed record TagsResponse(IReadOnlyList<Tag>? Models);

    private sealed record Tag(string? Name);
}

// Model: what is downloading. Error: why the last attempt failed (retried every ten minutes).
public sealed record OllamaModelDownload(string Model, int Percent, string? Error);

// InUse: whether the current routing sends anything to Ollama - when false, nothing is downloaded.
public sealed record OllamaModelStatus(string Configured, string? Active, OllamaModelDownload? Download, bool InUse = true);
