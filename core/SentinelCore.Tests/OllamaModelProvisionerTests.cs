using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using SentinelCore.Web;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using Xunit;

namespace SentinelCore.Tests;

public sealed class OllamaModelProvisionerTests
{
    // The point of the class: picking a model that isn't downloaded must not leave a gap where every
    // alert 404s. Alerts stay on the working model for the whole download and move the moment it ends.
    [Fact]
    public async Task NewModel_IsDownloaded_AndAlertsSwitchOnlyWhenItFinishes()
    {
        var ct = TestContext.Current.CancellationToken;
        var options = new ChangingOptions(new OllamaOptions { Model = "gemma3:4b" });
        var ollama = new FakeOllama("gemma3:4b");
        using var http = new HttpClient(ollama) { BaseAddress = new Uri("http://localhost:11434/") };
        using var provisioner = new OllamaModelProvisioner(http, options, NullLogger<OllamaModelProvisioner>.Instance);

        await provisioner.StartAsync(ct);
        await WaitFor(() => provisioner.Status.Active == "gemma3:4b", ct);

        options.Set(new OllamaOptions { Model = "gemma4:e2b-it-qat" });
        await WaitFor(() => provisioner.Status.Download?.Percent == 50, ct);

        Assert.Equal("gemma3:4b", provisioner.ModelForAlerts);
        Assert.Equal("gemma4:e2b-it-qat", provisioner.Status.Download!.Model);

        ollama.FinishPull();
        await WaitFor(() => provisioner.ModelForAlerts == "gemma4:e2b-it-qat", ct);
        Assert.Null(provisioner.Status.Download);
        Assert.Equal("gemma4:e2b-it-qat", ollama.PulledModel);

        await provisioner.StopAsync(ct);
    }

    [Fact]
    public async Task FailedDownload_KeepsTheOldModel_AndSaysWhy()
    {
        var ct = TestContext.Current.CancellationToken;
        var options = new ChangingOptions(new OllamaOptions { Model = "gemma3:4b" });
        var ollama = new FakeOllama("gemma3:4b") { PullError = "pull model manifest: file does not exist" };
        using var http = new HttpClient(ollama) { BaseAddress = new Uri("http://localhost:11434/") };
        using var provisioner = new OllamaModelProvisioner(http, options, NullLogger<OllamaModelProvisioner>.Instance);

        await provisioner.StartAsync(ct);
        await WaitFor(() => provisioner.Status.Active == "gemma3:4b", ct);
        options.Set(new OllamaOptions { Model = "gemma4:e2b-typo" });

        await WaitFor(() => provisioner.Status.Download?.Error is not null, ct);
        Assert.Equal("gemma3:4b", provisioner.ModelForAlerts);
        Assert.Contains("file does not exist", provisioner.Status.Download!.Error);

        await provisioner.StopAsync(ct);
    }

    // Ollama abandons a model load when the request that started it is cancelled, and an alert's
    // request gives up after 300s - so on a box that took ~10 minutes to load gemma4 (8 GB test VM,
    // 2026-11) every alert cancelled the load it was waiting on and the full AI never answered.
    [Fact]
    public async Task ASlowModelLoad_RunsOnItsOwnRequest_AndOutlivesTheAlertsWaitingOnIt()
    {
        var ct = TestContext.Current.CancellationToken;
        var ollama = new LoadingOllama();
        using var http = new HttpClient(ollama) { BaseAddress = new Uri("http://localhost:11434/") };
        using var provisioner = new OllamaModelProvisioner(
            http, new ChangingOptions(new OllamaOptions { Model = "gemma4:e2b-it-qat" }), NullLogger<OllamaModelProvisioner>.Instance);

        // Two alerts that won't wait long, one of which gives up altogether.
        using var impatient = new CancellationTokenSource();
        var first = provisioner.EnsureLoadedAsync(TimeSpan.FromSeconds(30), impatient.Token);
        var second = provisioner.EnsureLoadedAsync(TimeSpan.FromMilliseconds(200), ct);
        Assert.False(await second);
        impatient.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => first);

        Assert.Equal(1, ollama.LoadRequests);
        Assert.False(ollama.LoadCancelled, "an alert giving up cancelled the model load");

        ollama.FinishLoading();
        Assert.True(await provisioner.EnsureLoadedAsync(TimeSpan.FromSeconds(5), ct));
        Assert.Equal(1, ollama.LoadRequests);
    }

    [Fact]
    public async Task AModelAlreadyInMemory_IsReadyWithoutLoading()
    {
        var ct = TestContext.Current.CancellationToken;
        var ollama = new LoadingOllama { Loaded = "gemma4:e2b-it-qat" };
        using var http = new HttpClient(ollama) { BaseAddress = new Uri("http://localhost:11434/") };
        using var provisioner = new OllamaModelProvisioner(
            http, new ChangingOptions(new OllamaOptions { Model = "gemma4:e2b-it-qat" }), NullLogger<OllamaModelProvisioner>.Instance);

        Assert.True(await provisioner.EnsureLoadedAsync(TimeSpan.FromMilliseconds(100), ct));
        Assert.Equal(0, ollama.LoadRequests);
    }

    // /api/ps lists what is in memory; /api/generate with no prompt loads a model and answers when
    // it is in, here only once FinishLoading is called.
    private sealed class LoadingOllama : HttpMessageHandler
    {
        private readonly TaskCompletionSource _loaded = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _loadRequests;
        public string? Loaded { get; set; }
        public int LoadRequests => _loadRequests;
        public bool LoadCancelled { get; private set; }

        public void FinishLoading() => _loaded.TrySetResult();

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var path = request.RequestUri!.AbsolutePath;
            if (path.EndsWith("/api/ps"))
            {
                var models = Loaded is null ? Array.Empty<object>() : new object[] { new { name = Loaded, model = Loaded } };
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(JsonSerializer.Serialize(new { models })) };
            }
            if (path.EndsWith("/api/generate"))
            {
                Interlocked.Increment(ref _loadRequests);
                try
                {
                    await _loaded.Task.WaitAsync(ct);
                }
                catch (OperationCanceledException)
                {
                    LoadCancelled = true;
                    throw;
                }
                using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(ct));
                Loaded = body.RootElement.GetProperty("model").GetString();
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("""{"done":true,"done_reason":"load"}""") };
            }
            return new HttpResponseMessage(HttpStatusCode.NotFound);
        }
    }

    // gemma4:e2b-it-qat pulls as two big layers. Per layer, the wizard's bar ran to 98% and dropped
    // back to 0 for the second one - it read as a failed download on the first fresh install.
    [Fact]
    public async Task Progress_CoversTheWholeModel_NotJustTheCurrentLayer()
    {
        var ct = TestContext.Current.CancellationToken;
        var ollama = new FakeOllama
        {
            Manifest = """{"config":{"size":0},"layers":[{"size":250},{"size":750}]}""",
            Progress = """{"status":"pulling aaa","digest":"sha256:a","total":250,"completed":250}""" + "\n" +
                       """{"status":"pulling bbb","digest":"sha256:b","total":750,"completed":375}""" + "\n",
        };
        using var http = new HttpClient(ollama) { BaseAddress = new Uri("http://localhost:11434/") };
        using var provisioner = new OllamaModelProvisioner(
            http, new ChangingOptions(new OllamaOptions { Model = "gemma4:e2b-it-qat" }), NullLogger<OllamaModelProvisioner>.Instance);

        await provisioner.StartAsync(ct);
        // 625 of 1000 bytes - not the second layer's own 50%.
        await WaitFor(() => provisioner.Status.Download?.Percent == 62, ct);
        ollama.FinishPull();
        await provisioner.StopAsync(ct);
    }

    [Theory]
    [InlineData("gemma4:e2b-it-qat", "https://registry.ollama.ai/v2/library/gemma4/manifests/e2b-it-qat")]
    [InlineData("llava", "https://registry.ollama.ai/v2/library/llava/manifests/latest")]
    [InlineData("someone/model:7b", "https://registry.ollama.ai/v2/someone/model/manifests/7b")]
    [InlineData("hf.co/org/model:q4", null)]
    [InlineData("example.com/model", null)]
    public void ManifestUrl_OnlyForRegistryNames(string model, string? expected) =>
        Assert.Equal(expected, OllamaModelProvisioner.ManifestUrl(model)?.ToString());

    [Fact]
    public void ManifestTotal_SumsLayersAndConfig() =>
        Assert.Equal(1545L, OllamaModelProvisioner.ManifestTotalBytes(
            """{"config":{"size":545},"layers":[{"size":1000},{"size":0}]}"""));

    [Theory]
    [InlineData("""{"status":"pulling 3646b4c147cd","digest":"sha256:x","total":4000,"completed":1000}""", "pulling 3646b4c147cd", 25, null)]
    [InlineData("""{"status":"verifying sha256 digest"}""", "verifying sha256 digest", null, null)]
    [InlineData("""{"status":"success"}""", "success", null, null)]
    [InlineData("""{"error":"pull model manifest: file does not exist"}""", null, null, "pull model manifest: file does not exist")]
    public void PullLines_AreReadIntoProgress(string line, string? status, int? percent, string? error)
    {
        var update = OllamaModelProvisioner.ParsePullLine(line);
        Assert.NotNull(update);
        Assert.Equal(status, update.Status);
        Assert.Equal(percent, update.Percent);
        Assert.Equal(error, update.Error);
    }

    [Fact]
    public void PullLines_SkipBlanksAndGarbage()
    {
        Assert.Null(OllamaModelProvisioner.ParsePullLine(""));
        Assert.Null(OllamaModelProvisioner.ParsePullLine("not json"));
    }

    // The dropdown: recommended first (fastest first) whatever is installed, then everything else
    // installed, with download progress on whichever model is coming down.
    [Fact]
    public void VisionList_OffersRecommendedModels_AndShowsDownloadProgress()
    {
        var installed = new List<SettingsFieldOption>
        {
            new("gemma3:4b", "gemma3:4b - 3.1 GB"),
            new("llama3.1:8b", "llama3.1:8b - 4.6 GB"),
        };
        var status = new OllamaModelStatus("gemma4:e2b-it-qat", "gemma3:4b", new OllamaModelDownload("gemma4:e2b-it-qat", 42, null));

        var options = OllamaModelEndpoints.VisionOptions(installed, status);

        Assert.Equal(
            ["gemma4:e2b-it-qat", "gemma4:e4b-it-qat", "gemma3:4b", "qwen3-vl:4b-instruct", "llama3.1:8b"],
            options.Select(o => o.Value));
        Assert.Contains("downloading 42%", options[0].Label);
        Assert.Contains("not downloaded yet, 6.1 GB", options[1].Label);
        Assert.StartsWith("gemma3:4b - 3.1 GB - ", options[2].Label);
        Assert.Equal("llama3.1:8b - 4.6 GB", options[4].Label);
    }

    [Fact]
    public void VisionList_ShowsProgress_ForATypedModelOutsideTheList()
    {
        var status = new OllamaModelStatus("llava:7b", "gemma3:4b", new OllamaModelDownload("llava:7b", 7, null));

        var options = OllamaModelEndpoints.VisionOptions([], status);

        Assert.Equal("llava:7b", options[0].Value);
        Assert.Contains("downloading 7%", options[0].Label);
    }

    // Regression for the 2026-10-19 bench: without think:false Gemma 4 spent the whole 512-token
    // budget thinking and returned an empty answer on 3 of 4 alerts.
    [Fact]
    public async Task VisionRequest_TurnsThinkingOff_AndUsesTheActiveModel()
    {
        var ct = TestContext.Current.CancellationToken;
        var ollama = new FakeOllama("gemma3:4b");
        using var http = new HttpClient(ollama) { BaseAddress = new Uri("http://localhost:11434/") };
        var options = new ChangingOptions(new OllamaOptions { Model = "gemma3:4b" });
        var client = new OllamaVisionClient(
            http,
            options,
            new ImageOptimizer(new ChangingOptions<ImageProcessingOptions>(new ImageProcessingOptions()), NullLogger<ImageOptimizer>.Instance),
            NullLogger<OllamaVisionClient>.Instance);

        using var image = new Image<Rgb24>(64, 48);
        using var jpeg = new MemoryStream();
        await image.SaveAsJpegAsync(jpeg, ct);
        var alert = new IncomingAlert(Guid.NewGuid(), "Gate Camera", null, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow,
            "gate", "image/jpeg", "a.jpg", jpeg.ToArray());

        var (_, _, model) = await client.AnalyzeAsync(alert, new PromptProfile { Questions = ["Anything?"] }, ct);

        Assert.Equal("gemma3:4b", model);
        using var sent = JsonDocument.Parse(ollama.LastChatBody!);
        Assert.False(sent.RootElement.GetProperty("think").GetBoolean());
        Assert.Equal("gemma3:4b", sent.RootElement.GetProperty("model").GetString());
    }

    // A fresh install runs on the fast detector: nothing is downloaded until routing points at
    // Ollama, and then the routing change alone starts the pull.
    [Fact]
    public async Task NothingIsDownloaded_UntilRoutingUsesOllama()
    {
        var ct = TestContext.Current.CancellationToken;
        var options = new ChangingOptions(new OllamaOptions { Model = "gemma4:e2b-it-qat" });
        var routing = new ChangingOptions<VisionRoutingOptions>(new VisionRoutingOptions { Primary = "fastlocal" });
        var cloud = new ChangingOptions<CloudVisionOptions>(new CloudVisionOptions());
        var ollama = new FakeOllama();
        using var http = new HttpClient(ollama) { BaseAddress = new Uri("http://localhost:11434/") };
        using var provisioner = new OllamaModelProvisioner(http, options, NullLogger<OllamaModelProvisioner>.Instance, routing, cloud);

        await provisioner.StartAsync(ct);
        await Task.Delay(200, ct);
        Assert.Null(provisioner.Status.Download);
        Assert.False(provisioner.Status.InUse);
        Assert.False(provisioner.ModelMissing);

        routing.Set(new VisionRoutingOptions { Primary = "ollama" });
        await WaitFor(() => provisioner.Status.Download?.Percent == 50, ct);
        Assert.True(provisioner.ModelMissing);

        ollama.FinishPull();
        await WaitFor(() => provisioner.Status.Active == "gemma4:e2b-it-qat", ct);
        Assert.False(provisioner.ModelMissing);

        await provisioner.StopAsync(ct);
    }

    [Theory]
    [InlineData("ollama", false, false, "auto", false, true)]
    [InlineData("fastlocal", false, false, "auto", false, false)]
    // Fallback from the detector goes to Ollama without cloud (and for bypassed cameras with it).
    [InlineData("fastlocal", true, false, "auto", false, true)]
    // Escalation: "auto" means cloud when it's configured, Ollama otherwise.
    [InlineData("fastlocal", false, true, "auto", false, true)]
    [InlineData("fastlocal", false, true, "auto", true, false)]
    [InlineData("fastlocal", false, true, "cloud", false, false)]
    [InlineData("fastlocal", false, true, "ollama", true, true)]
    // A cloud primary only reaches Ollama when misconfigured or falling back.
    [InlineData("cloud", false, false, "auto", true, false)]
    [InlineData("cloud", true, false, "auto", true, true)]
    [InlineData("cloud", false, false, "auto", false, true)]
    public void OllamaInUse_FollowsTheRouting(
        string primary, bool fallback, bool escalation, string target, bool cloudConfigured, bool expected)
    {
        var routing = new VisionRoutingOptions
        {
            Primary = primary, FallbackEnabled = fallback, EscalationEnabled = escalation, EscalationTarget = target
        };
        var cloud = cloudConfigured ? new CloudVisionOptions { Enabled = true, ApiKey = "key" } : new CloudVisionOptions();

        Assert.Equal(expected, OllamaModelProvisioner.OllamaInUse(routing, cloud));
    }

    // The dashboard banner: quick detection, a download in progress, a failed one, Ollama down, or fine.
    [Fact]
    public void FullAiState_DescribesEachStage()
    {
        Assert.Equal("quick", OllamaModelEndpoints.FullAiWithoutOllama(new VisionRoutingSettingsDto { Primary = "fastlocal" }, false)!.State);
        Assert.Equal("full", OllamaModelEndpoints.FullAiWithoutOllama(new VisionRoutingSettingsDto { Primary = "cloud" }, true)!.State);
        Assert.Equal("full", OllamaModelEndpoints.FullAiWithoutOllama(
            new VisionRoutingSettingsDto { Primary = "fastlocal", EscalationEnabled = true }, true)!.State);
        Assert.Null(OllamaModelEndpoints.FullAiWithoutOllama(
            new VisionRoutingSettingsDto { Primary = "fastlocal", EscalationEnabled = true }, false));
        Assert.Null(OllamaModelEndpoints.FullAiWithoutOllama(new VisionRoutingSettingsDto { Primary = "ollama" }, true));

        Assert.Equal("ollama-unreachable", OllamaModelEndpoints.FullAiState("ollama", null, null).State);

        // The routing change has landed but the main app hasn't started pulling yet.
        var pending = OllamaModelEndpoints.FullAiState("ollama", [], new OllamaModelStatus("gemma4:e2b-it-qat", null, null));
        Assert.Equal(("downloading", 0), (pending.State, pending.Percent));

        var downloading = OllamaModelEndpoints.FullAiState("ollama", [],
            new OllamaModelStatus("gemma4:e2b-it-qat", null, new OllamaModelDownload("gemma4:e2b-it-qat", 37, null)));
        Assert.Equal(("downloading", 37), (downloading.State, downloading.Percent));

        var failed = OllamaModelEndpoints.FullAiState("ollama", [],
            new OllamaModelStatus("gemma4:e2b-it-qat", null, new OllamaModelDownload("gemma4:e2b-it-qat", 12, "disk full")));
        Assert.Equal(("download-failed", "disk full"), (failed.State, failed.Error));

        var full = OllamaModelEndpoints.FullAiState("ollama", [new("gemma4:e2b-it-qat", "gemma4:e2b-it-qat - 4.3 GB")],
            new OllamaModelStatus("gemma4:e2b-it-qat", "gemma4:e2b-it-qat", null));
        Assert.Equal("full", full.State);
    }

    private static async Task WaitFor(Func<bool> condition, CancellationToken ct)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (!condition())
        {
            if (DateTime.UtcNow > deadline) throw new TimeoutException("condition never became true");
            await Task.Delay(20, ct);
        }
    }

    // Stands in for Ollama: /api/tags lists what's installed, /api/pull streams half the download and
    // then waits for FinishPull, /api/chat answers with a minimal valid analysis.
    private sealed class FakeOllama(params string[] installed) : HttpMessageHandler
    {
        private readonly List<string> _installed = [.. installed];
        private readonly TaskCompletionSource _finish = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public string? PullError { get; init; }
        public string? Manifest { get; init; }
        public string Progress { get; init; } = """{"status":"pulling abc","total":200,"completed":100}""" + "\n";
        public string? PulledModel { get; private set; }
        public string? LastChatBody { get; private set; }

        public void FinishPull() => _finish.TrySetResult();

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var path = request.RequestUri!.AbsolutePath;
            if (request.RequestUri.Host == "registry.ollama.ai")
                return Manifest is null ? new HttpResponseMessage(HttpStatusCode.NotFound) : Json(Manifest);
            if (path.EndsWith("/api/tags"))
            {
                lock (_installed)
                    return Json(JsonSerializer.Serialize(new { models = _installed.Select(name => new { name }) }));
            }

            if (path.EndsWith("/api/pull"))
            {
                using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(ct));
                var model = body.RootElement.GetProperty("model").GetString()!;
                if (PullError is not null)
                    return Json(JsonSerializer.Serialize(new { error = PullError }));

                var stream = new PullStream(this, model);
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(stream) };
            }

            if (path.EndsWith("/api/chat"))
            {
                LastChatBody = await request.Content!.ReadAsStringAsync(ct);
                var analysis = """{"event_type":"none","summary":"Nothing.","gate":{"state":"unknown"},"person":{"visible":false},"vehicle":{"visible":false},"package":{"visible":false},"animal":{"visible":false},"confidence":0.5,"notification_recommended":false,"notification_reason":"nothing"}""";
                return Json(JsonSerializer.Serialize(new { message = new { content = analysis } }));
            }

            return new HttpResponseMessage(HttpStatusCode.NotFound);
        }

        private static HttpResponseMessage Json(string body) =>
            new(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

        // Writes "50% done", then blocks until FinishPull, then reports success and installs the model.
        private sealed class PullStream(FakeOllama owner, string model) : Stream
        {
            private byte[] _pending = Encoding.UTF8.GetBytes(owner.Progress);
            private int _stage;

            public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
            {
                if (_pending.Length == 0)
                {
                    if (_stage++ > 0) return 0;
                    await owner._finish.Task.WaitAsync(ct);
                    lock (owner._installed) owner._installed.Add(model);
                    owner.PulledModel = model;
                    _pending = Encoding.UTF8.GetBytes("""{"status":"success"}""" + "\n");
                }

                var count = Math.Min(buffer.Length, _pending.Length);
                _pending.AsMemory(0, count).CopyTo(buffer);
                _pending = _pending[count..];
                return count;
            }

            public override int Read(byte[] buffer, int offset, int count) =>
                ReadAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();

            public override bool CanRead => true;
            public override bool CanSeek => false;
            public override bool CanWrite => false;
            public override long Length => throw new NotSupportedException();
            public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
            public override void Flush() { }
            public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
            public override void SetLength(long value) => throw new NotSupportedException();
            public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        }
    }

    private sealed class ChangingOptions(OllamaOptions value) : ChangingOptions<OllamaOptions>(value);

    private class ChangingOptions<T>(T value) : IOptionsMonitor<T>
    {
        private readonly List<Action<T, string?>> _listeners = [];

        public T CurrentValue { get; private set; } = value;
        public T Get(string? name) => CurrentValue;

        public IDisposable? OnChange(Action<T, string?> listener)
        {
            _listeners.Add(listener);
            return null;
        }

        public void Set(T value)
        {
            CurrentValue = value;
            foreach (var listener in _listeners) listener(value, null);
        }
    }
}
