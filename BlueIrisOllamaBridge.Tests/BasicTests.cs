using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats.Jpeg;
using SixLabors.ImageSharp.PixelFormats;
using Xunit;

namespace BlueIrisOllamaBridge.Tests;

public sealed class BasicTests
{
    [Fact]
    public void VisionAnalysis_Defaults_AreSafe()
    {
        var analysis = new VisionAnalysis();
        Assert.False(analysis.NotificationRecommended);
        Assert.False(analysis.Person.Visible);
        Assert.False(analysis.Vehicle.Visible);
        Assert.False(analysis.Package.Visible);
        Assert.Equal("unknown", analysis.EventType);
    }

    [Fact]
    public void ClosedGate_PersonInside_IsSuppressed()
    {
        var result = GateResult("inside_driveway");

        Assert.False(AlertNotificationPolicy.ShouldNotify(result));
    }

    [Fact]
    public void ClosedGate_PersonOutside_IsNotSuppressed()
    {
        var result = GateResult("outside_gate");

        Assert.True(AlertNotificationPolicy.ShouldNotify(result));
    }

    [Fact]
    public void ClosedGate_VehicleSettledInUsualSpot_NoPerson_IsSuppressed()
    {
        var result = GateResult(
            vehicle: new VehicleAnalysis { Visible = true, Location = "parked_inside_property", Activity = "parked" });

        Assert.False(AlertNotificationPolicy.ShouldNotify(result));
    }

    [Fact]
    public void ClosedGate_VehicleFreshlyInDriveway_IsNotSuppressed()
    {
        // A vehicle that just arrived looks the same, in a single still frame, as one that has
        // been sitting there a while: the model can't confirm motion, so it reports "unknown"
        // activity rather than "entering". Location alone (inside_driveway, not the more settled
        // parked_inside_property) must be enough to avoid dropping real arrivals.
        var result = GateResult(
            vehicle: new VehicleAnalysis { Visible = true, Location = "inside_driveway", Activity = "unknown" });

        Assert.True(AlertNotificationPolicy.ShouldNotify(result));
    }

    [Fact]
    public void ClosedGate_VehicleAtOutsideEntrance_IsNotSuppressed()
    {
        var result = GateResult(
            vehicle: new VehicleAnalysis { Visible = true, Location = "outside_gate_entrance", Activity = "stopped" });

        Assert.True(AlertNotificationPolicy.ShouldNotify(result));
    }

    private static AlertResult GateResult(string? personLocation = null, VehicleAnalysis? vehicle = null) => new()
    {
        Profile = "gate",
        Analysis = new VisionAnalysis
        {
            NotificationRecommended = true,
            Gate = new GateAnalysis { State = "closed" },
            Person = personLocation is null
                ? new PersonAnalysis { Visible = false }
                : new PersonAnalysis { Visible = true, Location = personLocation },
            Vehicle = vehicle ?? new VehicleAnalysis()
        }
    };

    [Fact]
    public void TelegramAlertAction_BuildCaption_EscapesHtmlAndIncludesReason()
    {
        var result = new AlertResult
        {
            Camera = "Gate <front>",
            Analysis = new VisionAnalysis
            {
                Summary = "A car pulled into the driveway.",
                NotificationReason = "Vehicle stopped at the gate entrance."
            }
        };

        var caption = TelegramAlertAction.BuildCaption(result);

        Assert.Contains("Gate &lt;front&gt;", caption);
        Assert.Contains("A car pulled into the driveway.", caption);
        Assert.Contains("Vehicle stopped at the gate entrance.", caption);
    }

    [Fact]
    public void BlueIrisSnapshotClient_BuildSnapshotUrl_WithoutCredentials_OmitsQueryString()
    {
        var options = new BlueIrisOptions { BaseUrl = "http://192.168.50.7:777", Camera = "gateCamera" };

        var url = BlueIrisSnapshotClient.BuildSnapshotUrl(options);

        Assert.Equal("http://192.168.50.7:777/image/gateCamera", url);
    }

    [Fact]
    public void BlueIrisSnapshotClient_BuildSnapshotUrl_WithCredentials_IncludesUserAndPw()
    {
        var options = new BlueIrisOptions
        {
            BaseUrl = "http://192.168.50.7:777/",
            Camera = "gateCamera",
            Username = "admin",
            Password = "p@ss w/ord"
        };

        var url = BlueIrisSnapshotClient.BuildSnapshotUrl(options);

        Assert.Equal("http://192.168.50.7:777/image/gateCamera?user=admin&pw=p%40ss%20w%2Ford", url);
    }

    [Fact]
    public void TelegramChatWorker_IsPhotoCommand_RecognizesCommandVariants()
    {
        Assert.True(TelegramChatWorker.IsPhotoCommand("/photo"));
        Assert.True(TelegramChatWorker.IsPhotoCommand("/PHOTO"));
        Assert.True(TelegramChatWorker.IsPhotoCommand("/snapshot"));
        Assert.True(TelegramChatWorker.IsPhotoCommand("/photo@GateAlert332bot"));
        Assert.True(TelegramChatWorker.IsPhotoCommand("/photo please"));
        Assert.False(TelegramChatWorker.IsPhotoCommand("can I get a photo?"));
        Assert.False(TelegramChatWorker.IsPhotoCommand("what happened today"));
    }

    [Fact]
    public void TelegramChatWorker_IsAuthorizedChat_OnlyMatchesConfiguredChatId()
    {
        Assert.True(TelegramChatWorker.IsAuthorizedChat("123456789", "123456789"));
        Assert.False(TelegramChatWorker.IsAuthorizedChat("999999999", "123456789"));
        Assert.False(TelegramChatWorker.IsAuthorizedChat("123456789", ""));
    }

    [Fact]
    public void TelegramChatWorker_BuildEventLog_EmptyList_ReturnsPlaceholder()
    {
        var log = TelegramChatWorker.BuildEventLog([]);

        Assert.Equal("No camera events have been recorded in this period.", log);
    }

    [Fact]
    public void TelegramChatWorker_BuildEventLog_IncludesEachEventAndNotificationFlag()
    {
        var events = new List<AlertEventRecord>
        {
            new(Guid.NewGuid(), "gateCamera", DateTimeOffset.Now, "completed",
                "vehicle_entering_property", "A car pulled into the driveway.", true, "Vehicle entering", 0.8, null),
            new(Guid.NewGuid(), "gateCamera", DateTimeOffset.Now, "completed",
                "none", "Nothing of note.", false, "No activity", 0.5, null)
        };

        var log = TelegramChatWorker.BuildEventLog(events);

        Assert.Contains("A car pulled into the driveway.", log);
        Assert.Contains("notified=yes", log);
        Assert.Contains("Nothing of note.", log);
        Assert.Contains("notified=no", log);
    }

    [Fact]
    public void GetNextRun_BeforeMorningHour_FiresThisMorning()
    {
        var now = new DateTimeOffset(2026, 7, 24, 6, 0, 0, TimeSpan.FromHours(-5));

        var (fireAt, kind) = DailySummaryWorker.GetNextRun(now, morningHour: 7, eveningHour: 20);

        Assert.Equal(DigestKind.Morning, kind);
        Assert.Equal(new DateTimeOffset(2026, 7, 24, 7, 0, 0, TimeSpan.FromHours(-5)), fireAt);
    }

    [Fact]
    public void GetNextRun_BetweenMorningAndEvening_FiresThisEvening()
    {
        var now = new DateTimeOffset(2026, 7, 24, 7, 30, 0, TimeSpan.FromHours(-5));

        var (fireAt, kind) = DailySummaryWorker.GetNextRun(now, morningHour: 7, eveningHour: 20);

        Assert.Equal(DigestKind.Evening, kind);
        Assert.Equal(new DateTimeOffset(2026, 7, 24, 20, 0, 0, TimeSpan.FromHours(-5)), fireAt);
    }

    [Fact]
    public void GetNextRun_AfterEveningHour_FiresTomorrowMorning()
    {
        var now = new DateTimeOffset(2026, 7, 24, 20, 30, 0, TimeSpan.FromHours(-5));

        var (fireAt, kind) = DailySummaryWorker.GetNextRun(now, morningHour: 7, eveningHour: 20);

        Assert.Equal(DigestKind.Morning, kind);
        Assert.Equal(new DateTimeOffset(2026, 7, 25, 7, 0, 0, TimeSpan.FromHours(-5)), fireAt);
    }

    [Fact]
    public async Task SqliteEventStore_RecordAndQuery_RoundTripsWithinWindow()
    {
        var tempDir = Directory.CreateTempSubdirectory().FullName;
        try
        {
            var options = Options.Create(new BridgeOptions { ApiKey = "test-key-1234567890", DataDirectory = tempDir });
            using var store = new SqliteEventStore(options, new FakeHostEnvironment(tempDir));

            var inWindow = MakeResult(new DateTimeOffset(2026, 7, 24, 10, 0, 0, TimeSpan.FromHours(-5)));
            var outsideWindow = MakeResult(new DateTimeOffset(2026, 7, 23, 10, 0, 0, TimeSpan.FromHours(-5)));

            await store.RecordAsync(inWindow, TestContext.Current.CancellationToken);
            await store.RecordAsync(outsideWindow, TestContext.Current.CancellationToken);

            var results = await store.GetEventsAsync(
                new DateTimeOffset(2026, 7, 24, 0, 0, 0, TimeSpan.FromHours(-5)),
                new DateTimeOffset(2026, 7, 25, 0, 0, 0, TimeSpan.FromHours(-5)),
                TestContext.Current.CancellationToken);

            var record = Assert.Single(results);
            Assert.Equal(inWindow.Id, record.Id);
            Assert.Equal("completed", record.Status);
            Assert.Equal("vehicle_entering_property", record.EventType);
            Assert.True(record.NotificationRecommended);
        }
        finally
        {
            Directory.Delete(tempDir, recursive: true);
        }
    }

    private static AlertResult MakeResult(DateTimeOffset occurredAt) => new()
    {
        Id = Guid.NewGuid(),
        Camera = "gateCamera",
        OccurredAt = occurredAt,
        ReceivedAt = occurredAt,
        CompletedAt = occurredAt,
        Profile = "gate",
        Model = "gemma3:4b",
        Status = "completed",
        Analysis = new VisionAnalysis
        {
            EventType = "vehicle_entering_property",
            Summary = "A car pulled into the driveway.",
            NotificationRecommended = true,
            Confidence = 0.8
        }
    };

    [Fact]
    public async Task DailySummaryWorker_BuildMessageAsync_EmbedsNotableImagesAndListsAllEvents()
    {
        var tempDir = Directory.CreateTempSubdirectory().FullName;
        try
        {
            var imagePath = Path.Combine(tempDir, "notable.jpg");
            using (var image = new Image<Rgba32>(320, 240))
                await image.SaveAsync(imagePath, new JpegEncoder(), TestContext.Current.CancellationToken);

            var notableEvent = new AlertEventRecord(
                Guid.NewGuid(), "gateCamera", DateTimeOffset.Now, "completed",
                "vehicle_entering_property", "A car pulled into the driveway.",
                true, "Vehicle entering", 0.8, imagePath);
            var quietEvent = new AlertEventRecord(
                Guid.NewGuid(), "gateCamera", DateTimeOffset.Now, "completed",
                "none", "Nothing of note.", false, "No activity", 0.5, null);

            var worker = new DailySummaryWorker(
                eventStore: new StubEventStore(),
                imageOptimizer: new ImageOptimizer(Options.Create(new ImageProcessingOptions()), NullLogger<ImageOptimizer>.Instance),
                options: Options.Create(new DailySummaryOptions()),
                emailOptions: Options.Create(new EmailOptions { Username = "a@b.com", ToAddress = "a@b.com", FromName = "Gate Camera" }),
                logger: NullLogger<DailySummaryWorker>.Instance);

            using var message = await worker.BuildMessageAsync(
                "Today's activity summary",
                DateTimeOffset.Now.AddHours(-13),
                DateTimeOffset.Now,
                [notableEvent, quietEvent],
                TestContext.Current.CancellationToken);

            Assert.Contains("2 events", message.Subject);
            var view = Assert.Single(message.AlternateViews);
            Assert.Single(view.LinkedResources);

            var html = DailySummaryWorker.BuildHtml(
                "Today's activity summary",
                DateTimeOffset.Now.AddHours(-13),
                DateTimeOffset.Now,
                [notableEvent, quietEvent],
                [notableEvent],
                []);
            Assert.Contains("A car pulled into the driveway.", html);
            Assert.Contains("Nothing of note.", html);
        }
        finally
        {
            Directory.Delete(tempDir, recursive: true);
        }
    }

    private sealed class StubEventStore : IEventStore
    {
        public Task RecordAsync(AlertResult result, CancellationToken cancellationToken) => Task.CompletedTask;

        public Task<IReadOnlyList<AlertEventRecord>> GetEventsAsync(DateTimeOffset from, DateTimeOffset to, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<AlertEventRecord>>([]);
    }

    private sealed class FakeHostEnvironment(string contentRootPath) : IHostEnvironment
    {
        public string ApplicationName { get; set; } = "Tests";
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
        public string ContentRootPath { get; set; } = contentRootPath;
        public string EnvironmentName { get; set; } = "Test";
    }
}
