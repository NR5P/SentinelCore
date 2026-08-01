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
// with no restart, unlike the socket bindings in Urls. Username/password changes don't even
// need that much: the connection validator reads CurrentValue per attempt and always has.
public sealed class EmbeddedMqttBrokerService(
    IOptionsMonitor<MqttOptions> options,
    ILogger<EmbeddedMqttBrokerService> logger) : IHostedService, IDisposable
{
    private readonly SemaphoreSlim _reconcileLock = new(1, 1);
    private MqttServer? _server;
    private int _runningPort;
    private IDisposable? _optionsSubscription;

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        await ReconcileAsync(cancellationToken);

        // Fires on every configuration reload, even for unrelated sections - harmless here,
        // because ReconcileAsync no-ops unless the desired state actually differs from what's
        // running.
        _optionsSubscription = options.OnChange((_, _) => _ = ReconcileSafelyAsync());
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
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

            if (_server is not null && (!shouldRun || _runningPort != current.EmbeddedBrokerPort))
                await StopServerUnlockedAsync();

            if (shouldRun && _server is null)
                await StartServerUnlockedAsync(current.EmbeddedBrokerPort);
        }
        finally
        {
            _reconcileLock.Release();
        }
    }

    private async Task StartServerUnlockedAsync(int port)
    {
        var serverOptions = new MqttServerFactory().CreateServerOptionsBuilder()
            .WithDefaultEndpoint()
            .WithDefaultEndpointBoundIPAddress(IPAddress.Any)
            .WithDefaultEndpointPort(port)
            .Build();

        var server = new MqttServerFactory().CreateMqttServer(serverOptions);

        // This endpoint is bound to IPAddress.Any, so without this handler every machine on the
        // LAN could connect anonymously. Credentials are only enforced when Mqtt:Username is
        // actually set, which keeps an existing password-less Home Assistant setup working
        // exactly as it did - but once Device Hub is switching real lights over this broker,
        // setting a username/password is what makes that boundary real.
        server.ValidatingConnectionAsync += args =>
        {
            var expected = options.CurrentValue;
            if (string.IsNullOrWhiteSpace(expected.Username)) return Task.CompletedTask;

            var usernameMatches = string.Equals(args.UserName, expected.Username, StringComparison.Ordinal);
            var passwordMatches = string.Equals(args.Password, expected.Password, StringComparison.Ordinal);
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
        logger.LogInformation(
            "Embedded MQTT broker listening on port {Port} ({AuthMode})",
            port,
            string.IsNullOrWhiteSpace(options.CurrentValue.Username) ? "no authentication" : "username/password required");
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
