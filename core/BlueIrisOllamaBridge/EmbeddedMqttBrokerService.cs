using System.Net;
using Microsoft.Extensions.Options;
using MQTTnet.Server;

namespace BlueIrisOllamaBridge;

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
        await _server.StartAsync();
        logger.LogInformation("Embedded MQTT broker listening on port {Port}", current.EmbeddedBrokerPort);
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        if (_server is null) return;
        await _server.StopAsync();
        _server.Dispose();
    }
}
