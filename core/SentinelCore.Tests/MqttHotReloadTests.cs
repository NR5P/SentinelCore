using System.Net;
using System.Net.Sockets;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace SentinelCore.Tests;

// The behavior that lets MQTT be turned on from a phone: the broker and both MQTT clients react
// to options changes at runtime instead of reading their configuration once at startup. Uses
// real sockets on loopback with ports the OS hands out, so the assertions are about what a
// client on the network would actually observe.
public sealed class MqttHotReloadTests
{
    private sealed class MutableOptionsMonitor<T>(T value) : IOptionsMonitor<T>
    {
        private readonly List<Action<T, string?>> _listeners = [];
        public T CurrentValue { get; private set; } = value;
        public T Get(string? name) => CurrentValue;

        public IDisposable? OnChange(Action<T, string?> listener)
        {
            _listeners.Add(listener);
            return null;
        }

        public void Update(T newValue)
        {
            CurrentValue = newValue;
            foreach (var listener in _listeners.ToArray()) listener(newValue, null);
        }
    }

    private sealed class EmptyDeviceStore : IDeviceStore
    {
        public Task<IReadOnlyList<HubDevice>> GetAllAsync(CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<HubDevice>>([]);
        public Task<IReadOnlyList<HubDevice>> GetEnabledAsync(CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<HubDevice>>([]);
        public Task<HubDevice?> GetAsync(Guid id, CancellationToken cancellationToken) =>
            Task.FromResult<HubDevice?>(null);
        public Task<HubDevice> CreateAsync(HubDevice device, CancellationToken cancellationToken) =>
            Task.FromResult(device);
        public Task<HubDevice?> UpdateAsync(Guid id, HubDevice device, CancellationToken cancellationToken) =>
            Task.FromResult<HubDevice?>(device);
        public Task UpdateOrderAsync(IReadOnlyList<Guid> orderedIds, CancellationToken cancellationToken) =>
            Task.CompletedTask;
        public Task<bool> DeleteAsync(Guid id, CancellationToken cancellationToken) =>
            Task.FromResult(false);
    }

    // The OS picks a free port; the brief bind window between releasing it and the broker
    // claiming it is tolerable for a test on loopback.
    private static int FreePort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    private static Task<bool> CanConnectAsync(int port) => CanConnectAsync(IPAddress.Loopback, port);

    // This machine's own address on a real interface: connecting to it is what a device elsewhere
    // on the LAN would do, without needing one.
    private static IPAddress? LanAddress() =>
        System.Net.NetworkInformation.NetworkInterface.GetAllNetworkInterfaces()
            .Where(n => n.OperationalStatus == System.Net.NetworkInformation.OperationalStatus.Up)
            .SelectMany(n => n.GetIPProperties().UnicastAddresses)
            .Select(a => a.Address)
            .FirstOrDefault(a => a.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork && !IPAddress.IsLoopback(a));

    private static async Task<bool> CanConnectAsync(IPAddress address, int port)
    {
        using var client = new TcpClient();
        try
        {
            await client.ConnectAsync(address, port).WaitAsync(TimeSpan.FromMilliseconds(500));
            return true;
        }
        catch
        {
            return false;
        }
    }

    // The OnChange handlers are deliberately fire-and-forget, so state changes are asserted by
    // polling toward a deadline rather than awaiting anything directly.
    private static async Task<bool> EventuallyAsync(Func<Task<bool>> condition, TimeSpan? timeout = null)
    {
        var deadline = DateTimeOffset.UtcNow + (timeout ?? TimeSpan.FromSeconds(10));
        while (DateTimeOffset.UtcNow < deadline)
        {
            if (await condition()) return true;
            await Task.Delay(100);
        }
        return false;
    }

    private static MqttOptions Options(bool enabled, bool runBroker, int port) => new()
    {
        Enabled = enabled,
        RunEmbeddedBroker = runBroker,
        EmbeddedBrokerPort = port
    };

    [Fact]
    public async Task Broker_StartsWhenTurnedOnAtRuntime_AndStopsWhenTurnedOff()
    {
        var port = FreePort();
        var monitor = new MutableOptionsMonitor<MqttOptions>(Options(enabled: false, runBroker: false, port));
        var service = new EmbeddedMqttBrokerService(monitor, NullLogger<EmbeddedMqttBrokerService>.Instance);

        await service.StartAsync(CancellationToken.None);
        try
        {
            Assert.False(await CanConnectAsync(port));

            // What saving the MQTT section with Run Built-In Broker on now does - no restart.
            monitor.Update(Options(enabled: true, runBroker: true, port));
            Assert.True(await EventuallyAsync(() => CanConnectAsync(port)), "broker never started listening after being enabled");

            monitor.Update(Options(enabled: false, runBroker: false, port));
            Assert.True(await EventuallyAsync(async () => !await CanConnectAsync(port)), "broker kept listening after being disabled");
        }
        finally
        {
            await service.StopAsync(CancellationToken.None);
            service.Dispose();
        }
    }

    // With no username anyone could connect, so the broker stays on this machine; setting
    // credentials opens it to the LAN live, and clearing them closes it again.
    [Fact]
    public async Task Broker_IsLocalOnly_UntilCredentialsAreSet()
    {
        var lan = LanAddress();
        Assert.SkipWhen(lan is null, "No non-loopback IPv4 interface on this machine.");
        var port = FreePort();
        var monitor = new MutableOptionsMonitor<MqttOptions>(Options(enabled: true, runBroker: true, port));
        var service = new EmbeddedMqttBrokerService(monitor, NullLogger<EmbeddedMqttBrokerService>.Instance);

        await service.StartAsync(CancellationToken.None);
        try
        {
            Assert.True(await EventuallyAsync(() => CanConnectAsync(port)), "broker never started on loopback");
            Assert.False(await CanConnectAsync(lan!, port), "an anonymous broker was reachable from the network");

            monitor.Update(new MqttOptions
            {
                Enabled = true, RunEmbeddedBroker = true, EmbeddedBrokerPort = port, Username = "hub", Password = "a-password"
            });
            Assert.True(await EventuallyAsync(() => CanConnectAsync(lan!, port)), "setting credentials didn't open the broker to the network");

            monitor.Update(Options(enabled: true, runBroker: true, port));
            Assert.True(await EventuallyAsync(async () => !await CanConnectAsync(lan!, port)), "clearing credentials left the broker on the network");
        }
        finally
        {
            await service.StopAsync(CancellationToken.None);
            service.Dispose();
        }
    }

    [Fact]
    public async Task Broker_MovesToTheNewPort_WhenThePortSettingChanges()
    {
        var oldPort = FreePort();
        var newPort = FreePort();
        var monitor = new MutableOptionsMonitor<MqttOptions>(Options(enabled: true, runBroker: true, oldPort));
        var service = new EmbeddedMqttBrokerService(monitor, NullLogger<EmbeddedMqttBrokerService>.Instance);

        await service.StartAsync(CancellationToken.None);
        try
        {
            Assert.True(await EventuallyAsync(() => CanConnectAsync(oldPort)), "broker never started on the original port");

            monitor.Update(Options(enabled: true, runBroker: true, newPort));
            Assert.True(await EventuallyAsync(() => CanConnectAsync(newPort)), "broker never appeared on the new port");
            Assert.True(await EventuallyAsync(async () => !await CanConnectAsync(oldPort)), "broker kept listening on the old port");
        }
        finally
        {
            await service.StopAsync(CancellationToken.None);
            service.Dispose();
        }
    }

    [Fact]
    public async Task DeviceBus_FollowsTheHubToggle_WithoutARestart()
    {
        var port = FreePort();
        var mqttMonitor = new MutableOptionsMonitor<MqttOptions>(Options(enabled: true, runBroker: true, port));
        var broker = new EmbeddedMqttBrokerService(mqttMonitor, NullLogger<EmbeddedMqttBrokerService>.Instance);
        await broker.StartAsync(CancellationToken.None);

        var hubMonitor = new MutableOptionsMonitor<DeviceHubOptions>(new DeviceHubOptions { Enabled = false });
        var bus = new MqttDeviceBus(mqttMonitor, hubMonitor, new EmptyDeviceStore(), NullLogger<MqttDeviceBus>.Instance);

        try
        {
            await bus.StartAsync(CancellationToken.None);
            Assert.False(bus.IsConnected);

            // Turning the Device Hub on from a settings screen: the bus connects on its own.
            hubMonitor.Update(new DeviceHubOptions { Enabled = true });
            Assert.True(await EventuallyAsync(() => Task.FromResult(bus.IsConnected)), "device bus never connected after the hub was enabled");

            // And off again: the connection comes down rather than lingering.
            hubMonitor.Update(new DeviceHubOptions { Enabled = false });
            Assert.True(await EventuallyAsync(() => Task.FromResult(!bus.IsConnected)), "device bus stayed connected after the hub was disabled");
        }
        finally
        {
            await bus.StopAsync(CancellationToken.None);
            await bus.DisposeAsync();
            await broker.StopAsync(CancellationToken.None);
            broker.Dispose();
        }
    }
}
