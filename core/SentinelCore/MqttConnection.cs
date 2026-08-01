using MQTTnet;

namespace SentinelCore;

// Where the broker is and how to authenticate to it, in one place - MqttPublisher (alerts out)
// and MqttDeviceBus (device state in, commands out) are separate clients that must nonetheless
// agree exactly on which broker they're talking to.
public static class MqttConnection
{
    public static (string Host, int Port) ResolveBroker(MqttOptions options) =>
        options.RunEmbeddedBroker
            ? ("127.0.0.1", options.EmbeddedBrokerPort)
            : (options.BrokerHost, options.BrokerPort);

    // Everything about MqttOptions that affects an already-open connection, as one equatable
    // value. IOptionsMonitor.OnChange fires on every configuration reload - including a save of
    // some completely unrelated settings section - so the clients compare this before tearing
    // down a healthy connection. TopicPrefix is included because the publisher's last-will
    // (availability) topic is derived from it at connect time.
    public static object ConnectionKey(MqttOptions options) =>
        (options.Enabled, options.RunEmbeddedBroker, options.EmbeddedBrokerPort, options.BrokerHost,
         options.BrokerPort, options.Username, options.Password, options.UseTls, options.TopicPrefix);

    public static MqttClientOptionsBuilder CreateBuilder(MqttOptions options, string clientId)
    {
        var (host, port) = ResolveBroker(options);

        var builder = new MqttClientFactory().CreateClientOptionsBuilder()
            .WithClientId(clientId)
            .WithTcpServer(host, port)
            .WithCleanSession();

        if (!string.IsNullOrWhiteSpace(options.Username))
            builder = builder.WithCredentials(options.Username, options.Password);

        // Only meaningful against an external broker. The embedded broker listens on plain TCP
        // on loopback, so honouring UseTls there would just break this app's own connection to
        // it - and there's nothing to protect on a connection that never leaves the machine.
        if (options.UseTls && !options.RunEmbeddedBroker)
            builder = builder.WithTlsOptions(tls => tls.UseTls());

        return builder;
    }
}
