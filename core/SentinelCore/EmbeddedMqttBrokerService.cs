using System.Net;
using Microsoft.Extensions.Options;
using MQTTnet.Protocol;
using MQTTnet.Server;

namespace SentinelCore;

// Optional built-in MQTT broker so this app can be used with no separate broker (Mosquitto,
// Home Assistant's own add-on, etc.) at all - point Home Assistant's MQTT integration at this
// host instead. Off by default; when off, MqttPublisher connects to an external broker instead.
// Whether this runs, and on which port, is decided once at startup like other socket-binding
// options in this app (Bridge:DataDirectory, *BaseUrl) - changing it needs a restart.
public sealed class EmbeddedMqttBrokerService(IOptionsMonitor<MqttOptions> options, ILogger<EmbeddedMqttBrokerService> logger) : IHostedService
{
    private MqttServer? _server;

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        var current = options.CurrentValue;
        if (!current.Enabled || !current.RunEmbeddedBroker) return;

        var serverOptions = new MqttServerFactory().CreateServerOptionsBuilder()
            .WithDefaultEndpoint()
            .WithDefaultEndpointBoundIPAddress(IPAddress.Any)
            .WithDefaultEndpointPort(current.EmbeddedBrokerPort)
            .Build();

        _server = new MqttServerFactory().CreateMqttServer(serverOptions);

        // This endpoint is bound to IPAddress.Any, so without this handler every machine on the
        // LAN could connect anonymously. Credentials are only enforced when Mqtt:Username is
        // actually set, which keeps an existing password-less Home Assistant setup working
        // exactly as it did - but once Device Hub is switching real lights over this broker,
        // setting a username/password is what makes that boundary real.
        _server.ValidatingConnectionAsync += args =>
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

        await _server.StartAsync();
        logger.LogInformation(
            "Embedded MQTT broker listening on port {Port} ({AuthMode})",
            current.EmbeddedBrokerPort,
            string.IsNullOrWhiteSpace(current.Username) ? "no authentication" : "username/password required");
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        if (_server is null) return;
        await _server.StopAsync();
        _server.Dispose();
    }
}
