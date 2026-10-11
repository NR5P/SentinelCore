using System.Net;
using Microsoft.Extensions.Options;
using MQTTnet.Protocol;
using MQTTnet.Server;

namespace SentinelCore;

// Optional built-in MQTT broker so this app can be used with no separate broker (Mosquitto,
// Home Assistant's own add-on, etc.) at all - point Home Assistant's MQTT integration at this
// host instead. Off by default.
//
// Runs whenever the current options say it should, not just when they said so at startup: the
// settings screens (dashboard, phone, Mac) can flip RunEmbeddedBroker or move the port, and
// ReconcileAsync starts, stops, or rebinds the listener to match - the setting applies live,
// with no restart, unlike the socket bindings in Urls. The connection validator reads
// CurrentValue per attempt, so a new password applies to the next connection; setting or clearing
// the username also rebinds, because it decides whether the broker is reachable from the LAN.
public sealed class EmbeddedMqttBrokerService(
    IOptionsMonitor<MqttOptions> options,
    ILogger<EmbeddedMqttBrokerService> logger) : IHostedService, IDisposable
{
    private readonly SemaphoreSlim _reconcileLock = new(1, 1);
    private MqttServer? _server;
    private int _runningPort;
    private bool _runningOnAllInterfaces;
    private bool _disposed;
    private IDisposable? _optionsSubscription;

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        await ReconcileAsync(cancellationToken);

        // Fires on every configuration reload, even for unrelated sections - harmless here,
        // because ReconcileAsync no-ops unless the desired state actually differs from what's
        // running.
        _optionsSubscription = options.OnChange((_, _) => _ = ReconcileSafelyAsync());
    }

    // Dispose may run before StopAsync as well as after it - the DI container owns this
    // instance's lifetime and the host owns its start/stop, and nothing orders the two against
    // each other. Both orders have to be safe, and this one was not: StopAsync touched a
    // CancellationTokenSource and a SemaphoreSlim that Dispose had already released, so shutdown
    // threw ObjectDisposedException out of Host.StopAsync. Disposal also has to tolerate being
    // called more than once, which .NET's own guidance requires and which this type gets for
    // real: it is registered three times (concrete, interface, hosted service) and the container
    // tracks each registration separately.
    public async Task StopAsync(CancellationToken cancellationToken)
    {
        if (_disposed) return;

        _optionsSubscription?.Dispose();
        _optionsSubscription = null;

        await _reconcileLock.WaitAsync(cancellationToken);
        try
        {
            await StopServerUnlockedAsync();
        }
        finally
        {
            _reconcileLock.Release();
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        _optionsSubscription?.Dispose();
        _reconcileLock.Dispose();
    }

    private async Task ReconcileSafelyAsync()
    {
        try
        {
            await ReconcileAsync(CancellationToken.None);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Applying MQTT broker settings failed; the broker state is unchanged.");
        }
    }

    private async Task ReconcileAsync(CancellationToken cancellationToken)
    {
        await _reconcileLock.WaitAsync(cancellationToken);
        try
        {
            var current = options.CurrentValue;
            var shouldRun = current.Enabled && current.RunEmbeddedBroker;

            var onAllInterfaces = RequiresCredentials(current);
            if (_server is not null && (!shouldRun || _runningPort != current.EmbeddedBrokerPort || _runningOnAllInterfaces != onAllInterfaces))
                await StopServerUnlockedAsync();

            if (shouldRun && _server is null)
                await StartServerUnlockedAsync(current.EmbeddedBrokerPort, onAllInterfaces);
        }
        finally
        {
            _reconcileLock.Release();
        }
    }

    // Without a username the broker has no way to tell a light switch from anyone else on the
    // network, and Device Hub switches real locks, lights and garage doors over it. So a broker
    // with no credentials listens on this machine only (this app's own client, a Zigbee2MQTT on
    // the same box); setting Mqtt:Username/Password is what opens it to the LAN, with every
    // connection checked.
    public static bool RequiresCredentials(MqttOptions current) => !string.IsNullOrWhiteSpace(current.Username);

    private async Task StartServerUnlockedAsync(int port, bool onAllInterfaces)
    {
        var serverOptions = new MqttServerFactory().CreateServerOptionsBuilder()
            .WithDefaultEndpoint()
            .WithDefaultEndpointBoundIPAddress(onAllInterfaces ? IPAddress.Any : IPAddress.Loopback)
            .WithDefaultEndpointBoundIPV6Address(onAllInterfaces ? IPAddress.IPv6Any : IPAddress.IPv6Loopback)
            .WithDefaultEndpointPort(port)
            .Build();

        var server = new MqttServerFactory().CreateMqttServer(serverOptions);

        server.ValidatingConnectionAsync += args =>
        {
            var expected = options.CurrentValue;
            // Loopback-only (see RequiresCredentials). A username set since this listener
            // started is enforced straight away; the rebind to all interfaces follows it.
            if (!RequiresCredentials(expected)) return Task.CompletedTask;

            var usernameMatches = string.Equals(args.UserName, expected.Username, StringComparison.Ordinal);
            var passwordMatches = System.Security.Cryptography.CryptographicOperations.FixedTimeEquals(
                System.Text.Encoding.UTF8.GetBytes(args.Password ?? ""), System.Text.Encoding.UTF8.GetBytes(expected.Password ?? ""));
            if (usernameMatches && passwordMatches) return Task.CompletedTask;

            args.ReasonCode = MqttConnectReasonCode.BadUserNameOrPassword;
            logger.LogWarning("Rejected MQTT client {ClientId} - bad broker credentials", args.ClientId);
            return Task.CompletedTask;
        };

        try
        {
            await server.StartAsync();
        }
        catch (Exception ex)
        {
            // Most likely the port is already taken. The rest of the host must keep running -
            // dying over an optional broker would take the cameras down with it - so this logs
            // loudly and leaves the broker off; fixing the port in settings triggers another
            // reconcile and another attempt.
            logger.LogError(ex, "Could not start the embedded MQTT broker on port {Port} - is something already using it?", port);
            server.Dispose();
            return;
        }

        _server = server;
        _runningPort = port;
        _runningOnAllInterfaces = onAllInterfaces;
        if (onAllInterfaces)
            logger.LogInformation("Embedded MQTT broker listening on port {Port} on all interfaces (username/password required)", port);
        else
            logger.LogWarning(
                "Embedded MQTT broker listening on port {Port} on this machine only, because no Mqtt:Username is set. " +
                "Set a username and password to let devices elsewhere on your network connect.", port);
    }

    private async Task StopServerUnlockedAsync()
    {
        if (_server is null) return;
        await _server.StopAsync();
        _server.Dispose();
        _server = null;
        logger.LogInformation("Embedded MQTT broker on port {Port} stopped.", _runningPort);
    }
}
