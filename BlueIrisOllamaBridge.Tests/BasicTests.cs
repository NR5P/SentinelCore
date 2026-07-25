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
    public void QuietHoursPolicy_IsActive_HandlesOvernightWraparound()
    {
        var options = new QuietHoursOptions { Enabled = true, StartHour = 22, EndHour = 6 };

        Assert.True(QuietHoursPolicy.IsActive(options, AtHour(23)));
        Assert.True(QuietHoursPolicy.IsActive(options, AtHour(2)));
        Assert.True(QuietHoursPolicy.IsActive(options, AtHour(22)));
        Assert.False(QuietHoursPolicy.IsActive(options, AtHour(6)));
        Assert.False(QuietHoursPolicy.IsActive(options, AtHour(12)));
    }

    [Fact]
    public void QuietHoursPolicy_IsActive_HandlesSameDayWindow()
    {
        var options = new QuietHoursOptions { Enabled = true, StartHour = 9, EndHour = 17 };

        Assert.True(QuietHoursPolicy.IsActive(options, AtHour(9)));
        Assert.True(QuietHoursPolicy.IsActive(options, AtHour(12)));
        Assert.False(QuietHoursPolicy.IsActive(options, AtHour(17)));
        Assert.False(QuietHoursPolicy.IsActive(options, AtHour(3)));
    }

    [Fact]
    public void QuietHoursPolicy_IsActive_FalseWhenDisabledOrZeroWidth()
    {
        Assert.False(QuietHoursPolicy.IsActive(new QuietHoursOptions { Enabled = false, StartHour = 22, EndHour = 6 }, AtHour(23)));
        Assert.False(QuietHoursPolicy.IsActive(new QuietHoursOptions { Enabled = true, StartHour = 5, EndHour = 5 }, AtHour(5)));
    }

    [Fact]
    public void QuietHoursPolicy_EffectiveMinimumConfidence_OnlyRaisesNeverLowers()
    {
        var options = new QuietHoursOptions { Enabled = true, StartHour = 22, EndHour = 6, MinimumConfidence = 0.85 };

        // Outside the window: untouched.
        Assert.Equal(0.6, QuietHoursPolicy.EffectiveMinimumConfidence(options, 0.6, AtHour(12)));

        // Inside the window: raised because the channel's own floor (0.6) is lower.
        Assert.Equal(0.85, QuietHoursPolicy.EffectiveMinimumConfidence(options, 0.6, AtHour(2)));

        // Inside the window, but the channel already requires more than quiet hours do: unchanged.
        Assert.Equal(0.95, QuietHoursPolicy.EffectiveMinimumConfidence(options, 0.95, AtHour(2)));
    }

    // QuietHoursPolicy compares against the local wall-clock hour (via ToLocalTime()), so this
    // builds the DateTimeOffset using the test machine's own local offset - not a fixed one -
    // so the resulting .ToLocalTime().Hour is always exactly the requested hour regardless of
    // which timezone the tests happen to run in.
    private static DateTimeOffset AtHour(int hour)
    {
        var local = new DateTime(2026, 7, 24, hour, 0, 0, DateTimeKind.Local);
        return new DateTimeOffset(local);
    }

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
    public void DegradationTracker_AlertsOnlyAfterConsecutiveFailures_NotOnFirstBlip()
    {
        var tracker = new DegradationTracker(consecutiveBeforeAlert: 3);

        Assert.Equal(DegradationTransition.NoChange, tracker.Observe(isHealthy: false));
        Assert.Equal(DegradationTransition.NoChange, tracker.Observe(isHealthy: false));
        Assert.Equal(DegradationTransition.Alert, tracker.Observe(isHealthy: false));
        Assert.True(tracker.IsDegraded);
    }

    [Fact]
    public void DegradationTracker_DoesNotReAlertWhileAlreadyDegraded()
    {
        var tracker = new DegradationTracker(consecutiveBeforeAlert: 1);

        Assert.Equal(DegradationTransition.Alert, tracker.Observe(isHealthy: false));
        Assert.Equal(DegradationTransition.NoChange, tracker.Observe(isHealthy: false));
        Assert.Equal(DegradationTransition.NoChange, tracker.Observe(isHealthy: false));
    }

    [Fact]
    public void DegradationTracker_RecoversOnFirstHealthyObservation()
    {
        var tracker = new DegradationTracker(consecutiveBeforeAlert: 1);

        tracker.Observe(isHealthy: false);
        Assert.True(tracker.IsDegraded);

        Assert.Equal(DegradationTransition.Recovered, tracker.Observe(isHealthy: true));
        Assert.False(tracker.IsDegraded);
    }

    [Fact]
    public void DegradationTracker_BriefBlipBelowThreshold_NeverAlertsAndResetsCounter()
    {
        var tracker = new DegradationTracker(consecutiveBeforeAlert: 3);

        Assert.Equal(DegradationTransition.NoChange, tracker.Observe(isHealthy: false));
        Assert.Equal(DegradationTransition.NoChange, tracker.Observe(isHealthy: false));
        Assert.Equal(DegradationTransition.NoChange, tracker.Observe(isHealthy: true));
        Assert.Equal(DegradationTransition.NoChange, tracker.Observe(isHealthy: false));
        Assert.Equal(DegradationTransition.NoChange, tracker.Observe(isHealthy: false));
        Assert.False(tracker.IsDegraded);
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

    private static AlertResult MakeResult(
        DateTimeOffset occurredAt,
        string camera = "gateCamera",
        string eventType = "vehicle_entering_property",
        string summary = "A car pulled into the driveway.",
        bool notified = true) => new()
    {
        Id = Guid.NewGuid(),
        Camera = camera,
        OccurredAt = occurredAt,
        ReceivedAt = occurredAt,
        CompletedAt = occurredAt,
        Profile = "gate",
        Model = "gemma3:4b",
        Status = "completed",
        Analysis = new VisionAnalysis
        {
            EventType = eventType,
            Summary = summary,
            NotificationRecommended = notified,
            Confidence = 0.8
        }
    };

    [Fact]
    public async Task SqliteEventStore_SearchEvents_FiltersByCameraEventTypeNotifiedAndSearchText()
    {
        var tempDir = Directory.CreateTempSubdirectory().FullName;
        try
        {
            var options = Options.Create(new BridgeOptions { ApiKey = "test-key-1234567890", DataDirectory = tempDir });
            using var store = new SqliteEventStore(options, new FakeHostEnvironment(tempDir));

            var now = new DateTimeOffset(2026, 7, 24, 10, 0, 0, TimeSpan.FromHours(-5));
            var gateVehicle = MakeResult(now, camera: "gateCamera", eventType: "vehicle_entering_property", summary: "A car pulled into the driveway.", notified: true);
            var gateQuiet = MakeResult(now, camera: "gateCamera", eventType: "none", summary: "Nothing of note.", notified: false);
            var coopPredator = MakeResult(now, camera: "coopCamera", eventType: "predator_present", summary: "A fox was seen near the coop.", notified: true);

            await store.RecordAsync(gateVehicle, TestContext.Current.CancellationToken);
            await store.RecordAsync(gateQuiet, TestContext.Current.CancellationToken);
            await store.RecordAsync(coopPredator, TestContext.Current.CancellationToken);

            var byCamera = await store.SearchEventsAsync(new EventFilter(Camera: "coopCamera"), TestContext.Current.CancellationToken);
            Assert.Equal(coopPredator.Id, Assert.Single(byCamera).Id);

            var byEventType = await store.SearchEventsAsync(new EventFilter(EventType: "vehicle_entering_property"), TestContext.Current.CancellationToken);
            Assert.Equal(gateVehicle.Id, Assert.Single(byEventType).Id);

            var notifiedOnly = await store.SearchEventsAsync(new EventFilter(Camera: "gateCamera", NotifiedOnly: true), TestContext.Current.CancellationToken);
            Assert.Equal(gateVehicle.Id, Assert.Single(notifiedOnly).Id);

            var bySearchText = await store.SearchEventsAsync(new EventFilter(SearchText: "fox"), TestContext.Current.CancellationToken);
            Assert.Equal(coopPredator.Id, Assert.Single(bySearchText).Id);

            var everything = await store.SearchEventsAsync(new EventFilter(), TestContext.Current.CancellationToken);
            Assert.Equal(3, everything.Count);

            var options2 = await store.GetFilterOptionsAsync(TestContext.Current.CancellationToken);
            Assert.Equal(["coopCamera", "gateCamera"], options2.Cameras);
            Assert.Equal(["none", "predator_present", "vehicle_entering_property"], options2.EventTypes);
        }
        finally
        {
            Directory.Delete(tempDir, recursive: true);
        }
    }

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
                imageOptimizer: new ImageOptimizer(new StaticOptionsMonitor<ImageProcessingOptions>(new()), NullLogger<ImageOptimizer>.Instance),
                options: new StaticOptionsMonitor<DailySummaryOptions>(new()),
                emailOptions: new StaticOptionsMonitor<EmailOptions>(new EmailOptions { Username = "a@b.com", ToAddress = "a@b.com", FromName = "Gate Camera" }),
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

        public Task<AlertEventRecord?> GetEventAsync(Guid id, CancellationToken cancellationToken) =>
            Task.FromResult<AlertEventRecord?>(null);

        public Task<IReadOnlyList<AlertEventRecord>> SearchEventsAsync(EventFilter filter, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<AlertEventRecord>>([]);

        public Task<EventFilterOptions> GetFilterOptionsAsync(CancellationToken cancellationToken) =>
            Task.FromResult(new EventFilterOptions([], []));
    }

    private sealed class FakeHostEnvironment(string contentRootPath) : IHostEnvironment
    {
        public string ApplicationName { get; set; } = "Tests";
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
        public string ContentRootPath { get; set; } = contentRootPath;
        public string EnvironmentName { get; set; } = "Test";
    }

    // A fixed-value IOptionsMonitor<T> for tests that construct a class directly rather than
    // through DI. OnChange is never invoked since the value never changes in a test.
    private sealed class StaticOptionsMonitor<T>(T value) : IOptionsMonitor<T>
    {
        public T CurrentValue { get; } = value;
        public T Get(string? name) => CurrentValue;
        public IDisposable? OnChange(Action<T, string?> listener) => null;
    }
}
