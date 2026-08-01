using System.Net;
using System.Net.Sockets;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using MQTTnet;
using MQTTnet.Protocol;
using MQTTnet.Server;
using Xunit;

namespace SentinelCore.Tests;

public sealed class DeviceHubTests
{
    // ---------------- Store ----------------

    [Fact]
    public async Task SqliteDeviceStore_CreateGetUpdateDelete_RoundTrips()
    {
        var tempDir = Directory.CreateTempSubdirectory().FullName;
        try
        {
            var options = Options.Create(new BridgeOptions { ApiKey = "test-key-1234567890", DataDirectory = tempDir });
            using var store = new SqliteDeviceStore(options, new FakeHostEnvironment(tempDir));

            var created = await store.CreateAsync(
                Device("Kitchen Lamp", commandTopic: "cmnd/kitchen/POWER", stateTopic: "stat/kitchen/POWER"),
                TestContext.Current.CancellationToken);

            var fetched = await store.GetAsync(created.Id, TestContext.Current.CancellationToken);
            Assert.NotNull(fetched);
            Assert.Equal("Kitchen Lamp", fetched.Name);
            Assert.Equal("cmnd/kitchen/POWER", fetched.CommandTopic);
            Assert.Equal(DeviceTopics.SchemaRaw, fetched.Schema);

            var enabledBefore = await store.GetEnabledAsync(TestContext.Current.CancellationToken);
            Assert.Single(enabledBefore);

            var updated = await store.UpdateAsync(
                created.Id, created with { Enabled = false, Name = "Kitchen Lamp (off)" }, TestContext.Current.CancellationToken);
            Assert.NotNull(updated);
            Assert.False(updated.Enabled);
            Assert.Equal("Kitchen Lamp (off)", updated.Name);

            var enabledAfter = await store.GetEnabledAsync(TestContext.Current.CancellationToken);
            Assert.Empty(enabledAfter);

            Assert.True(await store.DeleteAsync(created.Id, TestContext.Current.CancellationToken));
            Assert.Null(await store.GetAsync(created.Id, TestContext.Current.CancellationToken));
        }
        finally
        {
            Directory.Delete(tempDir, recursive: true);
        }
    }

    // ---------------- Command payloads ----------------

    [Fact]
    public void BuildCommands_RawSchema_PublishesBarePayloadToCommandTopic()
    {
        var device = Device("Porch", commandTopic: "cmnd/porch/POWER", stateTopic: "stat/porch/POWER");

        var on = Assert.Single(DeviceTopics.BuildCommands(device, DevicePower.On, brightnessPercent: null));
        Assert.Equal("cmnd/porch/POWER", on.Topic);
        Assert.Equal("ON", on.Payload);
        Assert.False(on.Retain);

        var off = Assert.Single(DeviceTopics.BuildCommands(device, DevicePower.Off, brightnessPercent: null));
        Assert.Equal("OFF", off.Payload);
    }

    [Fact]
    public void BuildCommands_JsonSchema_CarriesStateAndBrightnessOnOneTopic()
    {
        // The Zigbee2MQTT shape: one /set topic taking {"state":"ON","brightness":254}.
        var device = Device("Lounge", commandTopic: "zigbee2mqtt/lounge/set", stateTopic: "zigbee2mqtt/lounge") with
        {
            Schema = DeviceTopics.SchemaJson,
            StateValueTemplate = "state",
            SupportsBrightness = true,
            BrightnessScale = 254
        };

        var publish = Assert.Single(DeviceTopics.BuildCommands(device, DevicePower.On, brightnessPercent: 100));

        Assert.Equal("zigbee2mqtt/lounge/set", publish.Topic);
        Assert.Contains("\"state\":\"ON\"", publish.Payload, StringComparison.Ordinal);
        Assert.Contains("\"brightness\":254", publish.Payload, StringComparison.Ordinal);
    }

    [Fact]
    public void BuildCommands_RawSchemaWithDedicatedBrightnessTopic_SendsTwoMessages()
    {
        // The Tasmota shape: POWER and Dimmer are separate commands.
        var device = Device("Hall", commandTopic: "cmnd/hall/POWER", stateTopic: "stat/hall/POWER") with
        {
            SupportsBrightness = true,
            BrightnessCommandTopic = "cmnd/hall/Dimmer",
            BrightnessStateTopic = "stat/hall/Dimmer",
            BrightnessScale = 100
        };

        var publishes = DeviceTopics.BuildCommands(device, DevicePower.On, brightnessPercent: 40);

        Assert.Equal(2, publishes.Count);
        Assert.Equal("cmnd/hall/POWER", publishes[0].Topic);
        Assert.Equal("ON", publishes[0].Payload);
        Assert.Equal("cmnd/hall/Dimmer", publishes[1].Topic);
        Assert.Equal("40", publishes[1].Payload);
    }

    [Fact]
    public void BuildCommands_BrightnessOnlyRequest_LeavesThePowerStateAlone()
    {
        var device = Device("Lounge", commandTopic: "zigbee2mqtt/lounge/set", stateTopic: "zigbee2mqtt/lounge") with
        {
            Schema = DeviceTopics.SchemaJson,
            StateValueTemplate = "state",
            SupportsBrightness = true,
            BrightnessScale = 255
        };

        var publish = Assert.Single(DeviceTopics.BuildCommands(device, power: null, brightnessPercent: 50));

        Assert.DoesNotContain("\"state\"", publish.Payload, StringComparison.Ordinal);
        Assert.Contains("\"brightness\":128", publish.Payload, StringComparison.Ordinal);
    }

    [Fact]
    public void BuildCommands_NoPowerAndNoBrightness_ProducesNothing() =>
        Assert.Empty(DeviceTopics.BuildCommands(
            Device("Porch", "cmnd/porch/POWER", "stat/porch/POWER"), power: null, brightnessPercent: null));

    // ---------------- State parsing ----------------

    [Fact]
    public void ParseState_RawSchema_MatchesConfiguredPayloads()
    {
        var device = Device("Porch", "cmnd/porch/POWER", "stat/porch/POWER");

        Assert.Equal(DevicePower.On, DeviceTopics.ParseState(device, "ON")!.Power);
        Assert.Equal(DevicePower.Off, DeviceTopics.ParseState(device, " OFF ")!.Power);
        // Case-insensitive because firmware disagrees on casing between versions.
        Assert.Equal(DevicePower.On, DeviceTopics.ParseState(device, "on")!.Power);
        Assert.Equal(DevicePower.Unknown, DeviceTopics.ParseState(device, "something else")!.Power);
    }

    [Fact]
    public void ParseState_JsonSchema_ReadsStateAndScalesBrightnessToPercent()
    {
        var device = Device("Lounge", "zigbee2mqtt/lounge/set", "zigbee2mqtt/lounge") with
        {
            Schema = DeviceTopics.SchemaJson,
            StateValueTemplate = "state",
            SupportsBrightness = true,
            BrightnessScale = 254
        };

        var reading = DeviceTopics.ParseState(device, """{"state":"ON","brightness":127,"linkquality":42}""");

        Assert.NotNull(reading);
        Assert.Equal(DevicePower.On, reading.Power);
        Assert.Equal(50, reading.BrightnessPercent);
    }

    [Fact]
    public void ParseState_JsonSchema_WalksADottedTemplatePath()
    {
        // Tasmota's STATUS11 response nests POWER inside StatusSTS.
        var device = Device("Hall", "cmnd/hall/POWER", "stat/hall/RESULT") with
        {
            Schema = DeviceTopics.SchemaJson,
            StateValueTemplate = "StatusSTS.POWER"
        };

        var reading = DeviceTopics.ParseState(device, """{"StatusSTS":{"POWER":"ON","Uptime":"1T00:00:00"}}""");

        Assert.Equal(DevicePower.On, reading!.Power);
    }

    [Fact]
    public void ParseState_MalformedJson_ReturnsNullSoTheLastGoodStateSurvives()
    {
        var device = Device("Lounge", "zigbee2mqtt/lounge/set", "zigbee2mqtt/lounge") with
        {
            Schema = DeviceTopics.SchemaJson,
            StateValueTemplate = "state"
        };

        Assert.Null(DeviceTopics.ParseState(device, "not json at all"));
    }

    [Fact]
    public void ParseState_BrightnessPct_IsTreatedAsAPercentageNotARawValue()
    {
        var device = Device("Lounge", "zigbee2mqtt/lounge/set", "zigbee2mqtt/lounge") with
        {
            Schema = DeviceTopics.SchemaJson,
            StateValueTemplate = "state",
            SupportsBrightness = true,
            BrightnessScale = 255
        };

        var reading = DeviceTopics.ParseState(device, """{"state":"ON","brightness_pct":60}""");

        Assert.Equal(60, reading!.BrightnessPercent);
    }

    [Theory]
    [InlineData(0, 255, 0)]
    [InlineData(100, 255, 255)]
    [InlineData(50, 255, 128)]
    [InlineData(50, 100, 50)]
    [InlineData(100, 254, 254)]
    public void BrightnessScaling_ConvertsPercentToTheDevicesOwnRange(int percent, int scale, int expected) =>
        Assert.Equal(expected, DeviceTopics.ToDeviceBrightness(percent, scale));

    [Fact]
    public void BrightnessScaling_ClampsOutOfRangeInput()
    {
        Assert.Equal(0, DeviceTopics.ToDeviceBrightness(-20, 255));
        Assert.Equal(255, DeviceTopics.ToDeviceBrightness(500, 255));
        Assert.Equal(100, DeviceTopics.ToPercentBrightness(9999, 255));
    }

    [Fact]
    public void ParseAvailability_RecognisesBothPayloadsAndIgnoresAnythingElse()
    {
        var device = Device("Porch", "cmnd/porch/POWER", "stat/porch/POWER");

        Assert.True(DeviceTopics.ParseAvailability(device, "online"));
        Assert.False(DeviceTopics.ParseAvailability(device, "offline"));
        Assert.Null(DeviceTopics.ParseAvailability(device, "who knows"));
    }

    // ---------------- Topic validation ----------------

    [Theory]
    [InlineData("cmnd/kitchen/POWER", true)]
    [InlineData("zigbee2mqtt/lounge/set", true)]
    [InlineData("cmnd/+/POWER", false)]
    [InlineData("cmnd/kitchen/#", false)]
    [InlineData("#", false)]
    [InlineData("  cmnd/kitchen/POWER", false)]
    [InlineData("", false)]
    public void IsValidConcreteTopic_RefusesWildcardsAndPadding(string topic, bool expected) =>
        Assert.Equal(expected, DeviceTopics.IsValidConcreteTopic(topic));

    // ---------------- Discovery ----------------

    [Fact]
    public void BuildCandidates_ParsesATasmotaHomeAssistantSwitchConfig()
    {
        // A real Tasmota discovery payload, abbreviated keys and "~" base and all.
        const string config = """
            {"name":"Tasmota Kitchen","stat_t":"~stat/POWER","cmd_t":"~cmnd/POWER","avty_t":"~tele/LWT",
             "pl_on":"ON","pl_off":"OFF","pl_avail":"Online","pl_not_avail":"Offline","~":"tasmota_kitchen/"}
            """;

        var candidate = Assert.Single(MqttDeviceDiscovery.BuildCandidates(
            new Dictionary<string, string> { ["homeassistant/switch/tasmota_kitchen/config"] = config },
            observedTopics: [],
            discoveryPrefix: "homeassistant",
            existingCommandTopics: new HashSet<string>(),
            existingStateTopics: new HashSet<string>()));

        Assert.Equal("Tasmota Kitchen", candidate.SuggestedName);
        Assert.Equal("switch", candidate.Kind);
        Assert.Equal("tasmota_kitchen/cmnd/POWER", candidate.CommandTopic);
        Assert.Equal("tasmota_kitchen/stat/POWER", candidate.StateTopic);
        Assert.Equal("tasmota_kitchen/tele/LWT", candidate.AvailabilityTopic);
        Assert.Equal(DeviceTopics.SchemaRaw, candidate.Schema);
        Assert.Equal(DeviceTopics.SourceHomeAssistantDiscovery, candidate.Source);
        Assert.False(candidate.AlreadyAdded);
    }

    [Fact]
    public void BuildCandidates_ParsesAZigbee2MqttJsonLightConfig()
    {
        const string config = """
            {"name":"Lounge Lamp","schema":"json","brightness":true,
             "command_topic":"zigbee2mqtt/lounge lamp/set","state_topic":"zigbee2mqtt/lounge lamp",
             "availability_topic":"zigbee2mqtt/bridge/state","brightness_scale":254}
            """;

        var candidate = Assert.Single(MqttDeviceDiscovery.BuildCandidates(
            new Dictionary<string, string> { ["homeassistant/light/0x00124b/light/config"] = config },
            observedTopics: [],
            discoveryPrefix: "homeassistant",
            existingCommandTopics: new HashSet<string>(),
            existingStateTopics: new HashSet<string>()));

        Assert.Equal("Lounge Lamp", candidate.SuggestedName);
        Assert.Equal("light", candidate.Kind);
        Assert.Equal(DeviceTopics.SchemaJson, candidate.Schema);
        Assert.Equal("state", candidate.StateValueTemplate);
        Assert.True(candidate.SupportsBrightness);
        Assert.Equal(254, candidate.BrightnessScale);
    }

    [Fact]
    public void BuildCandidates_FlagsDevicesAlreadyInTheHub()
    {
        const string config = """
            {"name":"Tasmota Kitchen","stat_t":"~stat/POWER","cmd_t":"~cmnd/POWER","~":"tasmota_kitchen/"}
            """;

        var candidate = Assert.Single(MqttDeviceDiscovery.BuildCandidates(
            new Dictionary<string, string> { ["homeassistant/switch/tasmota_kitchen/config"] = config },
            observedTopics: [],
            discoveryPrefix: "homeassistant",
            existingCommandTopics: new HashSet<string> { "tasmota_kitchen/cmnd/POWER" },
            existingStateTopics: new HashSet<string>()));

        Assert.True(candidate.AlreadyAdded);
    }

    [Fact]
    public void BuildCandidates_SweepsTasmotaStatTopicsIntoCommandablePairs()
    {
        var candidate = Assert.Single(MqttDeviceDiscovery.BuildCandidates(
            homeAssistantConfigs: new Dictionary<string, string>(),
            observedTopics: ["stat/kitchen_lamp/POWER", "tele/kitchen_lamp/STATE"],
            discoveryPrefix: "homeassistant",
            existingCommandTopics: new HashSet<string>(),
            existingStateTopics: new HashSet<string>()));

        Assert.Equal("Kitchen Lamp", candidate.SuggestedName);
        Assert.Equal("switch", candidate.Kind);
        Assert.Equal("cmnd/kitchen_lamp/POWER", candidate.CommandTopic);
        Assert.Equal("stat/kitchen_lamp/POWER", candidate.StateTopic);
        Assert.Equal("tele/kitchen_lamp/LWT", candidate.AvailabilityTopic);
        Assert.Equal(DeviceTopics.SourceTopicSweep, candidate.Source);
        Assert.False(candidate.SupportsBrightness);
    }

    [Fact]
    public void BuildCandidates_SweepDetectsATasmotaDimmerAndCallsItALight()
    {
        var candidate = Assert.Single(MqttDeviceDiscovery.BuildCandidates(
            homeAssistantConfigs: new Dictionary<string, string>(),
            observedTopics: ["stat/hall/POWER", "stat/hall/Dimmer"],
            discoveryPrefix: "homeassistant",
            existingCommandTopics: new HashSet<string>(),
            existingStateTopics: new HashSet<string>()));

        Assert.Equal("light", candidate.Kind);
        Assert.True(candidate.SupportsBrightness);
        Assert.Equal("cmnd/hall/Dimmer", candidate.BrightnessCommandTopic);
        Assert.Equal("stat/hall/Dimmer", candidate.BrightnessStateTopic);
        // Tasmota's Dimmer is already 0-100, so scaling it against 255 would quarter every value.
        Assert.Equal(100, candidate.BrightnessScale);
    }

    [Fact]
    public void BuildCandidates_SweepSkipsTopicsADiscoveryConfigAlreadyDescribed()
    {
        const string config = """
            {"name":"Kitchen","stat_t":"stat/kitchen_lamp/POWER","cmd_t":"cmnd/kitchen_lamp/POWER"}
            """;

        var candidates = MqttDeviceDiscovery.BuildCandidates(
            new Dictionary<string, string> { ["homeassistant/switch/kitchen/config"] = config },
            observedTopics: ["stat/kitchen_lamp/POWER"],
            discoveryPrefix: "homeassistant",
            existingCommandTopics: new HashSet<string>(),
            existingStateTopics: new HashSet<string>());

        var candidate = Assert.Single(candidates);
        Assert.Equal(DeviceTopics.SourceHomeAssistantDiscovery, candidate.Source);
    }

    [Fact]
    public void BuildCandidates_IgnoresConfigTopicsOutsideTheConfiguredDiscoveryPrefix()
    {
        const string config = """{"name":"Elsewhere","cmd_t":"x/set","stat_t":"x"}""";

        Assert.Empty(MqttDeviceDiscovery.BuildCandidates(
            new Dictionary<string, string> { ["someoneelse/switch/x/config"] = config },
            observedTopics: [],
            discoveryPrefix: "homeassistant",
            existingCommandTopics: new HashSet<string>(),
            existingStateTopics: new HashSet<string>()));
    }

    // ---------------- Staleness ----------------

    // Regression: a lamp that had simply been on all afternoon was showing as Unavailable,
    // because state silence was ageing out a device whose own availability topic said "online".
    // Most devices publish only when something changes, so that hit essentially every one.
    [Fact]
    public async Task GetState_DeviceWithAvailabilityTopic_IsNotAgedOutBySilence()
    {
        var device = Device("Lamp", "cmnd/lamp/POWER", "stat/lamp/POWER") with
        {
            AvailabilityTopic = "tele/lamp/LWT"
        };
        await using var bus = Bus(new DeviceHubOptions { Enabled = true, StateTimeoutSeconds = 10 });

        // Reported once, then went quiet for far longer than the timeout.
        bus.SetStateForTesting(device.Id, new DeviceLiveState(
            DevicePower.On, BrightnessPercent: null, Available: true,
            LastSeen: DateTimeOffset.UtcNow.AddHours(-3)));

        var state = bus.GetState(device);

        Assert.True(state.Available);
        Assert.Equal(DevicePower.On, state.Power);
    }

    [Fact]
    public async Task GetState_DeviceWithoutAvailabilityTopic_GoesUnavailableOnceSilent()
    {
        var device = Device("Lamp", "cmnd/lamp/POWER", "stat/lamp/POWER");
        await using var bus = Bus(new DeviceHubOptions { Enabled = true, StateTimeoutSeconds = 10 });

        bus.SetStateForTesting(device.Id, new DeviceLiveState(
            DevicePower.On, BrightnessPercent: null, Available: true,
            LastSeen: DateTimeOffset.UtcNow.AddMinutes(-5)));

        Assert.False(bus.GetState(device).Available);
    }

    [Fact]
    public async Task GetState_OptimisticDevice_IsNeverAgedOut()
    {
        // An optimistic device is one we already know never reports back, so ageing it out
        // would flip the UI to unavailable a minute after every command.
        var device = Device("Lamp", "cmnd/lamp/POWER", stateTopic: "") with { Optimistic = true };
        await using var bus = Bus(new DeviceHubOptions { Enabled = true, StateTimeoutSeconds = 10 });

        bus.SetStateForTesting(device.Id, new DeviceLiveState(
            DevicePower.On, BrightnessPercent: null, Available: true,
            LastSeen: DateTimeOffset.UtcNow.AddHours(-1)));

        Assert.True(bus.GetState(device).Available);
    }

    // ---------------- End to end, against a real broker ----------------

    // The test that actually proves the feature: a real MQTT server, a real MqttDeviceBus, and a
    // second client standing in for the lamp. Everything above this point tests payload shapes in
    // isolation; only this covers subscribe/publish/state-echo actually working together.
    [Fact]
    public async Task MqttDeviceBus_CommandsAndStateFlowThroughARealBroker()
    {
        var port = GetFreeTcpPort();
        var cancellationToken = TestContext.Current.CancellationToken;

        var serverOptions = new MqttServerFactory().CreateServerOptionsBuilder()
            .WithDefaultEndpoint()
            .WithDefaultEndpointBoundIPAddress(IPAddress.Loopback)
            .WithDefaultEndpointPort(port)
            .Build();
        using var server = new MqttServerFactory().CreateMqttServer(serverOptions);
        await server.StartAsync();

        var tempDir = Directory.CreateTempSubdirectory().FullName;
        try
        {
            var bridgeOptions = Options.Create(new BridgeOptions { ApiKey = "test-key-1234567890", DataDirectory = tempDir });
            using var store = new SqliteDeviceStore(bridgeOptions, new FakeHostEnvironment(tempDir));

            var device = await store.CreateAsync(
                Device("Kitchen Lamp", "cmnd/kitchen/POWER", "stat/kitchen/POWER") with
                {
                    AvailabilityTopic = "tele/kitchen/LWT"
                },
                cancellationToken);

            var mqttOptions = new MqttOptions { Enabled = true, BrokerHost = "127.0.0.1", BrokerPort = port };
            await using var bus = new MqttDeviceBus(
                new StaticOptionsMonitor<MqttOptions>(mqttOptions),
                new StaticOptionsMonitor<DeviceHubOptions>(new DeviceHubOptions { Enabled = true, CommandTimeoutSeconds = 3 }),
                store,
                NullLogger<MqttDeviceBus>.Instance);

            await bus.StartAsync(cancellationToken);
            await WaitUntilAsync(() => bus.IsConnected, TimeSpan.FromSeconds(10));
            Assert.True(bus.IsConnected);

            // Stand-in for the lamp: echoes state back the way real firmware does, so the bus's
            // acknowledgement path is genuinely exercised rather than assumed.
            var lamp = new MqttClientFactory().CreateMqttClient();
            var commandsReceived = new List<string>();
            lamp.ApplicationMessageReceivedAsync += async args =>
            {
                var payload = args.ApplicationMessage.ConvertPayloadToString() ?? string.Empty;
                commandsReceived.Add(payload);
                await lamp.PublishAsync(new MqttApplicationMessageBuilder()
                    .WithTopic("stat/kitchen/POWER")
                    .WithPayload(payload)
                    .WithQualityOfServiceLevel(MqttQualityOfServiceLevel.AtLeastOnce)
                    .Build(), CancellationToken.None);
            };

            await lamp.ConnectAsync(new MqttClientFactory().CreateClientOptionsBuilder()
                .WithClientId("fake-lamp")
                .WithTcpServer("127.0.0.1", port)
                .WithCleanSession()
                .Build(), cancellationToken);
            await lamp.SubscribeAsync("cmnd/kitchen/POWER", MqttQualityOfServiceLevel.AtLeastOnce, cancellationToken);

            try
            {
                // 1. A command reaches the device on the right topic with the right payload, and
                //    the echoed state is picked up and acknowledged.
                var result = await bus.SendCommandAsync(device, DevicePower.On, brightnessPercent: null, cancellationToken);

                Assert.True(result.Published);
                Assert.Null(result.Error);
                Assert.True(result.Acknowledged);
                Assert.Equal(["ON"], commandsReceived);
                Assert.Equal(DevicePower.On, bus.GetState(device).Power);

                // 2. The device's own unsolicited state change is picked up too - not just the
                //    echo of something we asked for.
                await lamp.PublishAsync(new MqttApplicationMessageBuilder()
                    .WithTopic("stat/kitchen/POWER")
                    .WithPayload("OFF")
                    .WithQualityOfServiceLevel(MqttQualityOfServiceLevel.AtLeastOnce)
                    .Build(), cancellationToken);

                await WaitUntilAsync(() => bus.GetState(device).Power == DevicePower.Off, TimeSpan.FromSeconds(5));
                Assert.Equal(DevicePower.Off, bus.GetState(device).Power);

                // 3. A last-will style availability message marks the device unavailable.
                Assert.True(bus.GetState(device).Available);
                await lamp.PublishAsync(new MqttApplicationMessageBuilder()
                    .WithTopic("tele/kitchen/LWT")
                    .WithPayload("offline")
                    .WithQualityOfServiceLevel(MqttQualityOfServiceLevel.AtLeastOnce)
                    .Build(), cancellationToken);

                await WaitUntilAsync(() => !bus.GetState(device).Available, TimeSpan.FromSeconds(5));
                Assert.False(bus.GetState(device).Available);

                // 4. Deleting the device drops its cached state rather than leaving a ghost.
                Assert.True(await store.DeleteAsync(device.Id, cancellationToken));
                await bus.ResubscribeAsync(cancellationToken);
                Assert.Equal(DevicePower.Unknown, bus.GetState(device).Power);
            }
            finally
            {
                await lamp.DisconnectAsync(cancellationToken: CancellationToken.None);
                lamp.Dispose();
                await bus.StopAsync(CancellationToken.None);
            }
        }
        finally
        {
            await server.StopAsync();
            Directory.Delete(tempDir, recursive: true);
        }
    }

    [Fact]
    public async Task MqttDeviceDiscovery_FindsARetainedHomeAssistantConfigOnARealBroker()
    {
        var port = GetFreeTcpPort();
        var cancellationToken = TestContext.Current.CancellationToken;

        var serverOptions = new MqttServerFactory().CreateServerOptionsBuilder()
            .WithDefaultEndpoint()
            .WithDefaultEndpointBoundIPAddress(IPAddress.Loopback)
            .WithDefaultEndpointPort(port)
            .Build();
        using var server = new MqttServerFactory().CreateMqttServer(serverOptions);
        await server.StartAsync();

        var tempDir = Directory.CreateTempSubdirectory().FullName;
        try
        {
            var bridgeOptions = Options.Create(new BridgeOptions { ApiKey = "test-key-1234567890", DataDirectory = tempDir });
            using var store = new SqliteDeviceStore(bridgeOptions, new FakeHostEnvironment(tempDir));

            // A device announcing itself the way real firmware does: a retained config message
            // that's already on the broker before the scan ever starts.
            var announcer = new MqttClientFactory().CreateMqttClient();
            await announcer.ConnectAsync(new MqttClientFactory().CreateClientOptionsBuilder()
                .WithClientId("announcer")
                .WithTcpServer("127.0.0.1", port)
                .WithCleanSession()
                .Build(), cancellationToken);
            await announcer.PublishAsync(new MqttApplicationMessageBuilder()
                .WithTopic("homeassistant/light/lounge/config")
                .WithPayload("""
                    {"name":"Lounge Lamp","schema":"json","command_topic":"zigbee2mqtt/lounge/set",
                     "state_topic":"zigbee2mqtt/lounge","brightness_scale":254}
                    """)
                .WithRetainFlag(true)
                .WithQualityOfServiceLevel(MqttQualityOfServiceLevel.AtLeastOnce)
                .Build(), cancellationToken);
            await announcer.DisconnectAsync(cancellationToken: CancellationToken.None);
            announcer.Dispose();

            var discovery = new MqttDeviceDiscovery(
                new StaticOptionsMonitor<MqttOptions>(new MqttOptions { Enabled = true, BrokerHost = "127.0.0.1", BrokerPort = port }),
                new StaticOptionsMonitor<DeviceHubOptions>(new DeviceHubOptions { Enabled = true, DiscoverySeconds = 2 }),
                store,
                NullLogger<MqttDeviceDiscovery>.Instance);

            var scanId = discovery.StartScan();
            await WaitUntilAsync(
                () => discovery.GetScanStatus(scanId)?.Status == DeviceScanStatus.Completed, TimeSpan.FromSeconds(20));

            var snapshot = discovery.GetScanStatus(scanId);
            Assert.NotNull(snapshot);
            Assert.Equal(DeviceScanStatus.Completed, snapshot.Status);

            var candidate = Assert.Single(snapshot.Results);
            Assert.Equal("Lounge Lamp", candidate.SuggestedName);
            Assert.Equal("zigbee2mqtt/lounge/set", candidate.CommandTopic);
            Assert.Equal(DeviceTopics.SchemaJson, candidate.Schema);
        }
        finally
        {
            await server.StopAsync();
            Directory.Delete(tempDir, recursive: true);
        }
    }

    // ---------------- Helpers ----------------

    private static HubDevice Device(string name, string commandTopic, string stateTopic) => new(
        Guid.NewGuid(), name, "light", "Test Room", Enabled: true,
        commandTopic, stateTopic, AvailabilityTopic: "",
        PayloadOn: "ON", PayloadOff: "OFF", PayloadAvailable: "online", PayloadNotAvailable: "offline",
        Schema: DeviceTopics.SchemaRaw, StateValueTemplate: "", Optimistic: false,
        SupportsBrightness: false, BrightnessCommandTopic: "", BrightnessStateTopic: "", BrightnessScale: 255,
        DiscoverySource: DeviceTopics.SourceManual,
        CreatedAt: DateTimeOffset.UtcNow, UpdatedAt: DateTimeOffset.UtcNow);

    // A bus with no broker behind it - enough for the GetState rules, which are pure logic over
    // the cached state and never touch the connection.
    private static MqttDeviceBus Bus(DeviceHubOptions hubOptions) => new(
        new StaticOptionsMonitor<MqttOptions>(new MqttOptions { Enabled = true }),
        new StaticOptionsMonitor<DeviceHubOptions>(hubOptions),
        new EmptyDeviceStore(),
        NullLogger<MqttDeviceBus>.Instance);

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
        public Task<bool> DeleteAsync(Guid id, CancellationToken cancellationToken) =>
            Task.FromResult(true);
    }

    // Binding to port 0 and reading back what the OS assigned is the only race-free way to pick
    // a port for a broker that hasn't started yet.
    private static int GetFreeTcpPort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    private static async Task WaitUntilAsync(Func<bool> condition, TimeSpan timeout)
    {
        var deadline = DateTimeOffset.UtcNow + timeout;
        while (DateTimeOffset.UtcNow < deadline)
        {
            if (condition()) return;
            await Task.Delay(50, CancellationToken.None);
        }
    }

    private sealed class FakeHostEnvironment(string contentRootPath) : IHostEnvironment
    {
        public string ApplicationName { get; set; } = "Tests";
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
        public string ContentRootPath { get; set; } = contentRootPath;
        public string EnvironmentName { get; set; } = "Test";
    }

    private sealed class StaticOptionsMonitor<T>(T value) : IOptionsMonitor<T>
    {
        public T CurrentValue { get; } = value;
        public T Get(string? name) => CurrentValue;
        public IDisposable? OnChange(Action<T, string?> listener) => null;
    }
}
