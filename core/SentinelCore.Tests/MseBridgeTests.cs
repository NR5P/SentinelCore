extern alias WebHost;

using System.Net.WebSockets;
using Xunit;
using CameraEndpoints = WebHost::SentinelCore.Web.CameraEndpoints;

namespace SentinelCore.Tests;

// The MSE bridge pumps bytes from go2rtc to a live viewer. Its failure mode is silent and open
// ended: a viewer that stops draining its socket - an Apple TV that slept, a tab the OS froze -
// leaves the send blocked forever, so the pump stops reading from go2rtc, and because go2rtc only
// runs a producer while it has a consumer, the wedged consumer keeps a camera being pulled at full
// bitrate on behalf of nobody for as long as the service runs.
//
// Observed live on 2026-08-30: three sockets from go2rtc with ~2.6 MB queued apiece, TCP in
// backoff, nothing sent for over seven minutes, while the producers kept pulling. What these tests
// pin down is that a destination which never accepts a message is eventually let go, and - just as
// important, since this tears down live video - that an ordinary session is not.
public sealed class MseBridgeTests
{
    private static readonly TimeSpan ShortDeadline = TimeSpan.FromMilliseconds(250);

    [Fact]
    public async Task AViewerThatStopsReadingIsLetGoAfterTheWriteDeadline()
    {
        var ct = TestContext.Current.CancellationToken;
        using var from = new ScriptedWebSocket(messages: 1);
        using var to = new WedgedWebSocket();

        var pumped = await CameraEndpoints.PumpWebSocketAsync(from, to, ShortDeadline, ct);

        // True is what makes the caller log the teardown and, far more importantly, what makes it
        // stop waiting - the whole bridge unwinds from this return and the go2rtc consumer closes.
        Assert.True(pumped);
    }

    [Fact]
    public async Task TheDeadlineIsPerMessageSoALongHealthySessionIsNeverTornDown()
    {
        var ct = TestContext.Current.CancellationToken;

        // Twenty messages, each accepted after a delay comfortably over half the deadline. A
        // per-session budget would expire partway through; a per-message one never comes close,
        // which is the distinction that keeps this from cutting off a slow but working viewer.
        using var from = new ScriptedWebSocket(messages: 20);
        using var to = new SlowWebSocket(TimeSpan.FromMilliseconds(150));

        var pumped = await CameraEndpoints.PumpWebSocketAsync(from, to, ShortDeadline, ct);

        Assert.False(pumped);
        Assert.Equal(20, to.Accepted);
    }

    [Fact]
    public async Task AViewerClosingNormallyIsNotReportedAsAWedge()
    {
        var ct = TestContext.Current.CancellationToken;
        using var from = new ScriptedWebSocket(messages: 2);
        using var to = new SlowWebSocket(TimeSpan.Zero);

        var pumped = await CameraEndpoints.PumpWebSocketAsync(from, to, ShortDeadline, ct);

        Assert.False(pumped);
    }

    [Fact]
    public async Task ShutdownIsNotBlamedOnTheViewer()
    {
        // The outer token is what a service stop or a closed tab cancels. That must not be
        // reported as the viewer wedging, or every restart would file one warning per live tile.
        using var cancelled = new CancellationTokenSource();
        using var from = new ScriptedWebSocket(messages: 1);
        using var to = new WedgedWebSocket();

        var pump = CameraEndpoints.PumpWebSocketAsync(from, to, TimeSpan.FromMinutes(5), cancelled.Token);
        await cancelled.CancelAsync();

        Assert.False(await pump);
    }

    // Hands out a fixed number of binary messages and then closes, so a test that is about the
    // destination's behaviour ends on its own rather than on a timeout. The wedge tests never
    // reach the close - they finish inside the first send.
    private sealed class ScriptedWebSocket(int messages) : FakeWebSocket
    {
        private int _sent;

        public override Task<WebSocketReceiveResult> ReceiveAsync(
            ArraySegment<byte> buffer, CancellationToken cancellationToken)
            => Task.FromResult(_sent++ < messages
                ? new WebSocketReceiveResult(64, WebSocketMessageType.Binary, endOfMessage: true)
                : new WebSocketReceiveResult(0, WebSocketMessageType.Close, endOfMessage: true));
    }

    // A destination that accepts nothing, ever: the send completes only when the pump's own
    // deadline cancels it. This is the wedged Apple TV, minus the seven-minute wait.
    private sealed class WedgedWebSocket : FakeWebSocket
    {
        public override Task SendAsync(
            ArraySegment<byte> buffer, WebSocketMessageType messageType, bool endOfMessage, CancellationToken cancellationToken)
            => Task.Delay(Timeout.Infinite, cancellationToken);
    }

    private sealed class SlowWebSocket(TimeSpan delay) : FakeWebSocket
    {
        public int Accepted { get; private set; }

        public override async Task SendAsync(
            ArraySegment<byte> buffer, WebSocketMessageType messageType, bool endOfMessage, CancellationToken cancellationToken)
        {
            if (delay > TimeSpan.Zero) await Task.Delay(delay, cancellationToken);
            Accepted++;
        }
    }

    private abstract class FakeWebSocket : WebSocket
    {
        public override WebSocketCloseStatus? CloseStatus => null;
        public override string? CloseStatusDescription => null;
        public override WebSocketState State => WebSocketState.Open;
        public override string? SubProtocol => null;

        public override void Abort() { }
        public override void Dispose() { }

        public override Task CloseAsync(WebSocketCloseStatus closeStatus, string? statusDescription, CancellationToken cancellationToken)
            => Task.CompletedTask;

        public override Task CloseOutputAsync(WebSocketCloseStatus closeStatus, string? statusDescription, CancellationToken cancellationToken)
            => Task.CompletedTask;

        // Only ScriptedWebSocket is ever read from; the others are destinations. Throwing rather
        // than returning something plausible keeps a future test from reading a socket by accident
        // and quietly getting whatever this made up.
        public override Task<WebSocketReceiveResult> ReceiveAsync(ArraySegment<byte> buffer, CancellationToken cancellationToken)
            => throw new NotSupportedException("This fake is a destination, not a source.");

        public override Task SendAsync(
            ArraySegment<byte> buffer, WebSocketMessageType messageType, bool endOfMessage, CancellationToken cancellationToken)
            => Task.CompletedTask;
    }
}
