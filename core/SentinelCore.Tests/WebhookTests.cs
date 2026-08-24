using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace SentinelCore.Tests;

// The Webhook channel had no coverage at all, which is worse for it than for the others: an email
// that never arrives is noticed, and a POST into someone else's home-automation system failing
// quietly is not. Everything here runs against a real HttpListener on loopback rather than a stub
// HttpMessageHandler - the questions worth asking ("does the API key reach the far end as a
// header", "what does a receiver actually receive") are about the wire, and a handler that never
// serialises anything answers a different question.
public sealed class WebhookTests
{
    private sealed class StaticOptionsMonitor<T>(T value) : IOptionsMonitor<T>
    {
        public T CurrentValue { get; } = value;
        public T Get(string? name) => CurrentValue;
        public IDisposable? OnChange(Action<T, string?> listener) => null;
    }

    private sealed record Received(string Method, string Body, string? ApiKey, string? ContentType);

    private sealed class Sink : IDisposable
    {
        private readonly HttpListener _listener = new();
        private readonly CancellationTokenSource _stopping = new();
        public string Url { get; }
        public string Host { get; }
        public List<Received> Requests { get; } = [];
        public HttpStatusCode Status { get; set; } = HttpStatusCode.OK;
        public string ResponseBody { get; set; } = "";
        public string ApiKeyHeader { get; set; } = "X-Api-Key";
        public TimeSpan Delay { get; set; } = TimeSpan.Zero;

        public Sink()
        {
            var port = FreePort();
            Host = $"127.0.0.1:{port}";
            Url = $"http://{Host}/hook";
            _listener.Prefixes.Add($"http://{Host}/");
            _listener.Start();
            _ = Task.Run(LoopAsync);
        }

        private async Task LoopAsync()
        {
            while (_listener.IsListening)
            {
                HttpListenerContext context;
                try { context = await _listener.GetContextAsync(); }
                catch { return; }

                using var reader = new StreamReader(context.Request.InputStream, Encoding.UTF8);
                var body = await reader.ReadToEndAsync();
                Requests.Add(new Received(
                    context.Request.HttpMethod, body,
                    context.Request.Headers[ApiKeyHeader], context.Request.ContentType));

                if (Delay > TimeSpan.Zero)
                {
                    // Only the timeout test delays, and that one is torn down while this is still
                    // waiting - stop on dispose rather than holding the listener open for the
                    // full delay after the test has finished with it.
                    try { await Task.Delay(Delay, _stopping.Token); }
                    catch (OperationCanceledException) { return; }
                }

                try
                {
                    context.Response.StatusCode = (int)Status;
                    if (ResponseBody.Length > 0)
                    {
                        var bytes = Encoding.UTF8.GetBytes(ResponseBody);
                        context.Response.ContentLength64 = bytes.Length;
                        await context.Response.OutputStream.WriteAsync(bytes);
                    }
                    context.Response.Close();
                }
                catch (Exception ex) when (ex is HttpListenerException or ObjectDisposedException)
                {
                    // The client gave up first - that's the point of the timeout test - or the
                    // test finished and disposed this out from under the reply.
                    return;
                }
            }
        }

        private static int FreePort()
        {
            var probe = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
            probe.Start();
            var port = ((IPEndPoint)probe.LocalEndpoint).Port;
            probe.Stop();
            return port;
        }

        // Abort, not Stop/Close: Close waits on requests still in flight and throws if one is
        // mid-reply, which under a full parallel test run is exactly what the timeout test leaves
        // behind. A sink failing to shut down tidily is not a finding about the webhook.
        public void Dispose()
        {
            _stopping.Cancel();
            try { _listener.Abort(); }
            catch (ObjectDisposedException) { /* already gone */ }
            _stopping.Dispose();
        }
    }

    private static WebhookAlertAction BuildAction(WebhookOptions options) =>
        new(new HttpClient(), new StaticOptionsMonitor<WebhookOptions>(options),
            NullLogger<WebhookAlertAction>.Instance);

    private static AlertResult Alert(bool notificationRecommended = true) => new()
    {
        Id = Guid.NewGuid(),
        Camera = "gate camera front door",
        Analysis = new VisionAnalysis
        {
            NotificationRecommended = notificationRecommended,
            Confidence = 0.9,
            Person = new PersonAnalysis { Visible = true, Location = "outside_gate", Activity = "standing" },
            Summary = "A person is standing outside the gate."
        }
    };

    // --- The alert channel itself -------------------------------------------------------------

    [Fact]
    public async Task WebhookAlertAction_WhenEnabled_PostsJsonWithTheApiKeyHeader()
    {
        using var sink = new Sink();
        var action = BuildAction(new WebhookOptions
        {
            Enabled = true,
            Url = sink.Url,
            ApiKey = "s3cret",
            ApiKeyHeaderName = "X-Api-Key"
        });

        await action.ExecuteAsync(Alert(), TestContext.Current.CancellationToken);

        var request = Assert.Single(sink.Requests);
        Assert.Equal("POST", request.Method);
        Assert.Equal("s3cret", request.ApiKey);
        Assert.Contains("application/json", request.ContentType);
        Assert.Contains("gate camera front door", request.Body);
        Assert.Contains("standing outside the gate", request.Body);
    }

    // The header name is configurable because the receiving end chooses it, and a webhook that
    // sent every key as X-Api-Key regardless would fail authentication on anything expecting
    // Authorization - visible only as 401s at the far end.
    [Fact]
    public async Task WebhookAlertAction_SendsTheApiKeyUnderTheConfiguredHeaderName()
    {
        using var sink = new Sink { ApiKeyHeader = "Authorization" };
        var action = BuildAction(new WebhookOptions
        {
            Enabled = true,
            Url = sink.Url,
            ApiKey = "Bearer abc123",
            ApiKeyHeaderName = "Authorization"
        });

        await action.ExecuteAsync(Alert(), TestContext.Current.CancellationToken);

        Assert.Equal("Bearer abc123", Assert.Single(sink.Requests).ApiKey);
    }

    [Fact]
    public async Task WebhookAlertAction_WhenDisabled_SendsNothing()
    {
        using var sink = new Sink();
        var action = BuildAction(new WebhookOptions { Enabled = false, Url = sink.Url });

        await action.ExecuteAsync(Alert(), TestContext.Current.CancellationToken);

        Assert.Empty(sink.Requests);
    }

    [Fact]
    public async Task WebhookAlertAction_WithNoUrl_SendsNothingAndDoesNotThrow()
    {
        var action = BuildAction(new WebhookOptions { Enabled = true, Url = "   " });

        await action.ExecuteAsync(Alert(), TestContext.Current.CancellationToken);
    }

    [Theory]
    [InlineData(true, false, 0)]   // gated, and the AI didn't recommend notifying
    [InlineData(true, true, 1)]
    [InlineData(false, false, 1)]  // ungated: everything goes, which is the logging use case
    [InlineData(false, true, 1)]
    public async Task WebhookAlertAction_HonoursOnlyWhenNotificationRecommended(
        bool onlyWhenRecommended, bool recommended, int expectedRequests)
    {
        using var sink = new Sink();
        var action = BuildAction(new WebhookOptions
        {
            Enabled = true,
            Url = sink.Url,
            OnlyWhenNotificationRecommended = onlyWhenRecommended
        });

        await action.ExecuteAsync(Alert(recommended), TestContext.Current.CancellationToken);

        Assert.Equal(expectedRequests, sink.Requests.Count);
    }

    // A receiver having a bad day must not take the other channels down with it. The dispatch
    // loops in AlertProcessor and GateCheckAnalyzer catch per action, but a 500 isn't an exception
    // in the first place - it's logged and shrugged off here.
    [Fact]
    public async Task WebhookAlertAction_WhenTheReceiverErrors_DoesNotThrow()
    {
        using var sink = new Sink { Status = HttpStatusCode.InternalServerError, ResponseBody = "boom" };
        var action = BuildAction(new WebhookOptions { Enabled = true, Url = sink.Url });

        await action.ExecuteAsync(Alert(), TestContext.Current.CancellationToken);

        Assert.Single(sink.Requests);
    }

    // --- The "send test webhook" button -------------------------------------------------------

    private static async Task<(bool Success, string Detail)> SendTestAsync(
        WebhookOptions options, TimeSpan? timeout = null)
    {
        using var client = new HttpClient();
        if (timeout is { } value) client.Timeout = value;
        return await NotificationTester.SendTestWebhookAsync(
            client, options, TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task SendTestWebhook_PostsAnAlertShapedBodyTheReceiverCanParse()
    {
        using var sink = new Sink();

        var (success, detail) = await SendTestAsync(new WebhookOptions
        {
            Enabled = true,
            Url = sink.Url,
            ApiKey = "s3cret"
        });

        Assert.True(success, detail);
        var request = Assert.Single(sink.Requests);
        Assert.Equal("POST", request.Method);
        Assert.Equal("s3cret", request.ApiKey);

        // The promise the button's description makes is "exactly the shape real alerts arrive in".
        // Anything parsing real events has to be able to parse this one, so parse it as one.
        var received = JsonSerializer.Deserialize<AlertResult>(
            request.Body, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
        Assert.NotNull(received);
        Assert.NotNull(received.Analysis);
        Assert.True(received.Analysis.NotificationRecommended);
        Assert.NotEqual(Guid.Empty, received.Id);

        // ...and it has to be obvious at the far end that nobody walked past a camera.
        Assert.Contains("test", received.Camera, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("test", received.Analysis.Summary, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task SendTestWebhook_ReportsTheHostAndStatusItGot()
    {
        using var sink = new Sink { Status = HttpStatusCode.Accepted };

        var (success, detail) = await SendTestAsync(new WebhookOptions { Enabled = true, Url = sink.Url });

        Assert.True(success, detail);
        Assert.Contains("127.0.0.1", detail);
        Assert.Contains("202", detail);
    }

    // Checking a URL before switching the channel on is exactly when this is most useful, so a
    // disabled webhook still sends - and the reply has to say so, or a successful test reads as
    // "alerts are flowing now" when nothing will ever be sent.
    [Fact]
    public async Task SendTestWebhook_SendsWhileDisabled_ButSaysSo()
    {
        using var sink = new Sink();

        var (success, detail) = await SendTestAsync(new WebhookOptions { Enabled = false, Url = sink.Url });

        Assert.True(success, detail);
        Assert.Single(sink.Requests);
        Assert.Contains("switched off", detail);
    }

    // The other gate ExecuteAsync applies. A test that silently declined to send because this box
    // was ticked would answer the opposite of what pressing the button looked like.
    [Fact]
    public async Task SendTestWebhook_IgnoresOnlyWhenNotificationRecommended()
    {
        using var sink = new Sink();

        var (success, _) = await SendTestAsync(new WebhookOptions
        {
            Enabled = true,
            Url = sink.Url,
            OnlyWhenNotificationRecommended = true
        });

        Assert.True(success);
        Assert.Single(sink.Requests);
    }

    [Theory]
    [InlineData("", "No URL is set")]
    [InlineData("   ", "No URL is set")]
    [InlineData("192.168.50.8/hook", "not a valid")]      // no scheme - the usual way to mistype it
    [InlineData("ftp://example.invalid/hook", "not a valid")]
    public async Task SendTestWebhook_RefusesAUrlItCannotPost_WithoutSendingAnything(
        string url, string expected)
    {
        var (success, detail) = await SendTestAsync(new WebhookOptions { Enabled = true, Url = url });

        Assert.False(success);
        Assert.Contains(expected, detail, StringComparison.OrdinalIgnoreCase);
    }

    // A webhook receiver is usually something the person reading this wrote, so its own words beat
    // any paraphrase of a status code.
    [Fact]
    public async Task SendTestWebhook_WhenRefused_QuotesWhatTheReceiverSaid()
    {
        using var sink = new Sink
        {
            Status = HttpStatusCode.Unauthorized,
            ResponseBody = """{"error":"bad api key"}"""
        };

        var (success, detail) = await SendTestAsync(new WebhookOptions { Enabled = true, Url = sink.Url });

        Assert.False(success);
        Assert.Contains("401", detail);
        Assert.Contains("bad api key", detail);
    }

    [Fact]
    public async Task SendTestWebhook_WhenRefusedWithNoBody_StillExplainsItself()
    {
        using var sink = new Sink { Status = HttpStatusCode.NotFound };

        var (success, detail) = await SendTestAsync(new WebhookOptions { Enabled = true, Url = sink.Url });

        Assert.False(success);
        Assert.Contains("404", detail);
        Assert.Contains("empty body", detail);
    }

    // Port 1 refuses immediately. The button has to come back with the reason rather than throwing
    // past the endpoint and turning into a 500 with no detail in it.
    [Fact]
    public async Task SendTestWebhook_WhenNothingIsListening_FailsWithAnExplanation()
    {
        var (success, detail) = await SendTestAsync(
            new WebhookOptions { Enabled = true, Url = "http://127.0.0.1:1/hook" });

        Assert.False(success);
        Assert.NotEmpty(detail);
    }

    // The most likely way to mistype this setting is an address nothing answers on, which hangs
    // rather than refusing. HttpClient raises its own timeout as a cancellation, so without the
    // dedicated catch this is indistinguishable from the request being cancelled.
    [Fact]
    public async Task SendTestWebhook_WhenTheReceiverNeverAnswers_SaysItTimedOut()
    {
        using var sink = new Sink { Delay = TimeSpan.FromSeconds(30) };

        var (success, detail) = await SendTestAsync(
            new WebhookOptions { Enabled = true, Url = sink.Url, TimeoutSeconds = 1 },
            timeout: TimeSpan.FromSeconds(1));

        Assert.False(success);
        Assert.Contains("No answer", detail);
        Assert.Contains("1 seconds", detail);
    }
}
