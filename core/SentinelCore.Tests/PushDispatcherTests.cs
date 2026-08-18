using System.Net;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Xunit;

namespace SentinelCore.Tests;

// Dropping a push registration is permanent and invisible from the outside: the device simply
// stops receiving alerts until its app happens to launch again and re-register. Neither caller of
// PushNotificationDispatcher.SendAsync reads PushDeliveryReport.Removed, so the warning raised at
// the point of deletion is the only record that it happened - which makes it worth pinning.
public sealed class PushDispatcherTests
{
    [Fact]
    public async Task SendAsync_WhenApnsReportsUnregistered_RemovesTheDeviceAndSaysSo()
    {
        using var key = NewSigningKey(out var keyPath);
        var store = new FakePushDeviceStore(
            NewDevice("apple", "stale-token", "Apple TV"));
        var logger = new CapturingLogger();
        var dispatcher = NewDispatcher(store, Respond(HttpStatusCode.Gone, """{"reason":"Unregistered"}"""), logger);

        var report = await dispatcher.SendAsync(
            NewOptions(keyPath), PushMessage.Test(), CancellationToken.None);

        Assert.Equal(1, report.Removed);
        Assert.Empty(store.Devices);

        // Named, because "a device was dropped" is only actionable if you know which one.
        var line = Assert.Single(logger.Warnings, m => m.Contains("Removed the push registration"));
        Assert.Contains("Apple TV", line);

        File.Delete(keyPath);
    }

    // The case that reads as harmless: one of two devices goes stale and PushAlertAction reports
    // "delivered to 1/2 registered devices", which looks exactly like a transient partial failure.
    // The surviving device must keep working, and the dropped one must still be named.
    [Fact]
    public async Task SendAsync_WhenOneOfTwoIsUnregistered_KeepsTheOtherAndNamesTheDropped()
    {
        using var key = NewSigningKey(out var keyPath);
        var store = new FakePushDeviceStore(
            NewDevice("apple", "stale-token", "Apple TV"),
            NewDevice("apple", "good-token", "iPhone"));
        var logger = new CapturingLogger();
        var handler = new PerTokenHandler(token => token.Contains("stale")
            ? (HttpStatusCode.Gone, """{"reason":"Unregistered"}""")
            : (HttpStatusCode.OK, "{}"));
        var dispatcher = NewDispatcher(store, handler, logger);

        var report = await dispatcher.SendAsync(
            NewOptions(keyPath), PushMessage.Test(), CancellationToken.None);

        Assert.Equal(2, report.Attempted);
        Assert.Equal(1, report.Delivered);
        Assert.Equal(1, report.Removed);

        var survivor = Assert.Single(store.Devices);
        Assert.Equal("iPhone", survivor.DeviceName);

        var line = Assert.Single(logger.Warnings, m => m.Contains("Removed the push registration"));
        Assert.Contains("Apple TV", line);

        File.Delete(keyPath);
    }

    // A rejection that is not "this token is dead" must leave the registration alone - a bad
    // payload or a provider hiccup should cost one push, not the device.
    [Fact]
    public async Task SendAsync_WhenApnsRejectsForAnotherReason_KeepsTheDevice()
    {
        using var key = NewSigningKey(out var keyPath);
        var store = new FakePushDeviceStore(NewDevice("apple", "good-token", "iPhone"));
        var logger = new CapturingLogger();
        var dispatcher = NewDispatcher(
            store, Respond(HttpStatusCode.BadRequest, """{"reason":"PayloadTooLarge"}"""), logger);

        var report = await dispatcher.SendAsync(
            NewOptions(keyPath), PushMessage.Test(), CancellationToken.None);

        Assert.Equal(1, report.Failed);
        Assert.Equal(0, report.Removed);
        Assert.Single(store.Devices);
        Assert.DoesNotContain(logger.Warnings, m => m.Contains("Removed the push registration"));

        File.Delete(keyPath);
    }

    private static PushNotificationDispatcher NewDispatcher(
        IPushDeviceStore store, HttpMessageHandler handler, ILogger<PushNotificationDispatcher> logger) =>
        new(store, new SingleClientFactory(handler), new FakeEnvironment(), logger);

    // A real P-256 key, because GetAppleProviderTokenAsync signs the provider JWT for real before
    // any request goes out - a placeholder string would fail in ImportFromPem, not at the assert.
    private static ECDsa NewSigningKey(out string path)
    {
        var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        path = Path.Combine(Path.GetTempPath(), $"apns-{Guid.NewGuid():N}.p8");
        File.WriteAllText(path, key.ExportPkcs8PrivateKeyPem());
        return key;
    }

    private static PushNotificationOptions NewOptions(string keyPath) => new()
    {
        Enabled = true,
        Apple = new ApplePushOptions
        {
            Enabled = true,
            TeamId = "TEAM123456",
            KeyId = "KEY1234567",
            BundleId = "com.sentinelcore.client",
            PrivateKeyPath = keyPath
        }
    };

    private static PushDevice NewDevice(string platform, string token, string name) =>
        new(Guid.NewGuid(), platform, token, name, "ios", DateTimeOffset.UtcNow, DateTimeOffset.UtcNow);

    private static HttpMessageHandler Respond(HttpStatusCode status, string body) =>
        new PerTokenHandler(_ => (status, body));

    private sealed class PerTokenHandler(Func<string, (HttpStatusCode Status, string Body)> reply)
        : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var (status, body) = reply(request.RequestUri!.AbsolutePath);
            return Task.FromResult(new HttpResponseMessage(status)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json")
            });
        }
    }

    private sealed class SingleClientFactory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
    }

    private sealed class FakePushDeviceStore(params PushDevice[] devices) : IPushDeviceStore
    {
        public List<PushDevice> Devices { get; } = [.. devices];

        public Task<IReadOnlyList<PushDevice>> GetAllAsync(CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<PushDevice>>([.. Devices]);

        public Task<int> CountAsync(CancellationToken cancellationToken) =>
            Task.FromResult(Devices.Count);

        public Task RemoveAsync(string platform, string token, CancellationToken cancellationToken)
        {
            Devices.RemoveAll(d => d.Platform == platform && d.Token == token);
            return Task.CompletedTask;
        }

        public Task<PushDevice> UpsertAsync(
            string platform, string token, string? deviceName,
            CancellationToken cancellationToken, string appVariant = "ios") =>
            throw new NotSupportedException();
    }

    private sealed class CapturingLogger : ILogger<PushNotificationDispatcher>
    {
        public List<string> Warnings { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (logLevel == LogLevel.Warning) Warnings.Add(formatter(state, exception));
        }
    }

    private sealed class FakeEnvironment : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = "Test";
        public string ApplicationName { get; set; } = "SentinelCore.Tests";
        public string ContentRootPath { get; set; } = Path.GetTempPath();
        public IFileProvider ContentRootFileProvider { get; set; } =
            new NullFileProvider();
    }
}
