using System.Net;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace SentinelCore.Tests;

// Found on a fresh Linux install: the first alerts arrived while the ~100MB quick-detection model
// was downloading, and each one started its own download into the same temp file, truncating the
// others. AddHttpClient<TService, TImpl>() had made the detector transient, so every consumer had
// its own "one download at a time" lock.
public sealed class ModelServiceLifetimeTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "model-lifetime-" + Guid.NewGuid().ToString("N"));

    public ModelServiceLifetimeTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    [Fact]
    public void TheDetectorAndEmbeddingService_AreOneSharedInstanceEach()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IHostEnvironment>(new StubEnvironment(_root));
        services.Configure<BridgeOptions>(_ => { });
        services.Configure<FastLocalOptions>(_ => { });
        services.AddSingletonWithHttpClient<IFastLocalDetector, FastLocalDetector>();
        services.AddSingletonWithHttpClient<IImageEmbeddingService, ImageEmbeddingService>();
        using var provider = services.BuildServiceProvider();

        Assert.Same(provider.GetRequiredService<IFastLocalDetector>(), provider.GetRequiredService<IFastLocalDetector>());
        using var scope = provider.CreateScope();
        Assert.Same(provider.GetRequiredService<IImageEmbeddingService>(), scope.ServiceProvider.GetRequiredService<IImageEmbeddingService>());
    }

    [Fact]
    public async Task ConcurrentFirstCallers_DownloadTheModelOnce_AndACallerGivingUpDoesNotCancelIt()
    {
        var handler = new SlowCountingHandler();
        var detector = new FastLocalDetector(
            new HttpClient(handler),
            new StaticOptionsMonitor<FastLocalOptions>(new FastLocalOptions
            {
                ModelPath = Path.Combine(_root, "model.onnx"),
                DownloadUrl = "https://example.invalid/model.onnx",
                DownloadSha256 = "",
            }),
            Options.Create(new BridgeOptions()),
            new StubEnvironment(_root),
            NullLogger<FastLocalDetector>.Instance);

        using var impatient = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));
        var first = detector.DetectAsync([1, 2, 3], null, impatient.Token);
        var second = detector.DetectAsync([1, 2, 3], null, CancellationToken.None);
        var third = detector.DetectAsync([1, 2, 3], null, CancellationToken.None);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => first);
        // The bytes aren't a real model, so loading it fails - after one complete download.
        await Assert.ThrowsAnyAsync<Exception>(() => second);
        await Assert.ThrowsAnyAsync<Exception>(() => third);

        Assert.Equal(1, handler.Requests);
        Assert.True(handler.Completed, "the download was cancelled along with the first caller");
        detector.Dispose();
    }

    private sealed class SlowCountingHandler : HttpMessageHandler
    {
        private int _requests;
        public int Requests => _requests;
        public bool Completed { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _requests);
            await Task.Delay(400, cancellationToken);
            Completed = true;
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(new byte[64]) };
        }
    }

    private sealed class StaticOptionsMonitor<T>(T value) : IOptionsMonitor<T>
    {
        public T CurrentValue => value;
        public T Get(string? name) => value;
        public IDisposable? OnChange(Action<T, string?> listener) => null;
    }

    private sealed class StubEnvironment(string contentRoot) : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = "Testing";
        public string ApplicationName { get; set; } = "SentinelCore.Tests";
        public string ContentRootPath { get; set; } = contentRoot;
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }
}
