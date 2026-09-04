extern alias WebHost;

using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using SentinelCore.Web;
using Xunit;

namespace SentinelCore.Tests;

// TriggerConfirmationTests covers the store rules and the HasVisibleSubject predicate in
// isolation. This covers the part between them that nothing else does: the service actually
// polling the main app for a verdict, deserializing what comes back, and writing the result.
//
// That middle section is where a silent, total failure lives. Every visibility flag on
// VisionAnalysis is a bool defaulting to false, so if the JSON coming back from
// /api/results/{id} ever stops binding - a renamed field, a changed wrapper, a naming-policy
// difference between the two processes - the analysis deserializes into an object with nothing
// visible, HasVisibleSubject says no, and every single trigger is rejected and hidden. The alert
// pipeline meanwhile looks perfect, because the alerts themselves are unaffected. These tests
// serialize with the same settings ASP.NET's Results.Ok uses, so the contract between the two
// processes is checked rather than assumed.
public sealed class TriggerConfirmationServiceTests
{
    // What ASP.NET Core's Results.Ok(result) produces on the main app's side.
    private static readonly JsonSerializerOptions AspNetDefaults = new(JsonSerializerDefaults.Web);

    private static SqliteTriggerStore NewStore(out string root)
    {
        root = Path.Combine(Path.GetTempPath(), $"trigsvc-{Guid.NewGuid():N}");
        return new SqliteTriggerStore(root);
    }

    private static TriggerConfirmationService NewService(
        SqliteTriggerStore store, AlertResult? whatTheMainAppReturns, out string settingsPath,
        bool discardRejectedClips = true)
    {
        settingsPath = Path.Combine(Path.GetTempPath(), $"settings-{Guid.NewGuid():N}.json");
        File.WriteAllText(settingsPath, $$"""
            {
              "Bridge": { "ApiKey": "test-key-1234567890" },
              "TriggerConfirmation": {
                "Enabled": true,
                "TimeoutSeconds": 30,
                "DiscardRejectedClips": {{(discardRejectedClips ? "true" : "false")}}
              }
            }
            """);

        var handler = new StubMainApp(whatTheMainAppReturns);
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["MainAppBaseUrl"] = "http://127.0.0.1:5080",
        }).Build();

        return new TriggerConfirmationService(
            new SingleHandlerFactory(handler),
            new AppSettingsFile(settingsPath),
            store,
            configuration,
            NullLogger<TriggerConfirmationService>.Instance);
    }

    // The case that matters: a truck in the driveway. The analysis says so, so the trigger has to
    // come back visible. If this ever fails, the Triggers tab silently empties out while every
    // alert still reads correctly - which is exactly what that looks like from the outside.
    [Fact]
    public async Task AVehicleInFrame_ConfirmsTheTrigger_AndItAppearsInTheTab()
    {
        using var store = NewStore(out var root);
        try
        {
            var cameraId = Guid.NewGuid();
            var trigger = await store.AddAsync(
                cameraId, "snap.jpg", 0.2, null, TriggerConfirmation.Pending, TestContext.Current.CancellationToken);

            var completed = new AlertResult
            {
                Id = Guid.NewGuid(),
                Status = "completed",
                Analysis = new VisionAnalysis
                {
                    Summary = "A truck is in the drive.",
                    Vehicle = new VehicleAnalysis { Visible = true }
                }
            };

            var service = NewService(store, completed, out var settingsPath);
            try
            {
                await service.ResolveAsync(
                    trigger.Id, new AlertForwardResult(Accepted: true, AlertId: completed.Id),
                    TestContext.Current.CancellationToken);

                var stored = await ((ITriggerEventStore)store).GetAsync(trigger.Id, TestContext.Current.CancellationToken);
                Assert.Equal(TriggerConfirmation.Confirmed, stored!.Confirmation);

                // The tab itself, not just the column - this is what the person actually sees.
                var listed = await store.GetRecentAsync(50, cameraId, includeUnconfirmed: false, TestContext.Current.CancellationToken);
                Assert.Contains(listed, e => e.Id == trigger.Id);
            }
            finally
            {
                File.Delete(settingsPath);
            }
        }
        finally
        {
            TempDirectories.Delete(root);
        }
    }

    // The feature working as intended - an empty frame stays out of the tab.
    [Fact]
    public async Task AnEmptyFrame_RejectsTheTrigger_AndItIsHiddenFromTheTab()
    {
        using var store = NewStore(out var root);
        try
        {
            var cameraId = Guid.NewGuid();
            var trigger = await store.AddAsync(
                cameraId, "snap.jpg", 0.2, null, TriggerConfirmation.Pending, TestContext.Current.CancellationToken);

            var completed = new AlertResult
            {
                Id = Guid.NewGuid(),
                Status = "completed",
                Analysis = new VisionAnalysis { Summary = "Branches moving in the wind." }
            };

            var service = NewService(store, completed, out var settingsPath);
            try
            {
                await service.ResolveAsync(
                    trigger.Id, new AlertForwardResult(Accepted: true, AlertId: completed.Id),
                    TestContext.Current.CancellationToken);

                var stored = await ((ITriggerEventStore)store).GetAsync(trigger.Id, TestContext.Current.CancellationToken);
                Assert.Equal(TriggerConfirmation.Rejected, stored!.Confirmation);

                var listed = await store.GetRecentAsync(50, cameraId, includeUnconfirmed: false, TestContext.Current.CancellationToken);
                Assert.DoesNotContain(listed, e => e.Id == trigger.Id);

                // ...but still auditable behind "show hidden", which is the promise the feature makes.
                var withHidden = await store.GetRecentAsync(50, cameraId, includeUnconfirmed: true, TestContext.Current.CancellationToken);
                Assert.Contains(withHidden, e => e.Id == trigger.Id);
            }
            finally
            {
                File.Delete(settingsPath);
            }
        }
        finally
        {
            TempDirectories.Delete(root);
        }
    }

    // The contract between the two processes, checked directly. VisionAnalysis names its fields
    // with explicit [JsonPropertyName] attributes while the AlertResult wrapper around it does
    // not, so the payload mixes camelCase and snake_case. The poller has to read both.
    [Fact]
    public void TheMainAppsPayload_BindsBackIntoTheFlagsTheVerdictIsMadeFrom()
    {
        var onTheWire = JsonSerializer.Serialize(new AlertResult
        {
            Status = "completed",
            Analysis = new VisionAnalysis
            {
                Summary = "A truck is in the drive.",
                Vehicle = new VehicleAnalysis { Visible = true },
                Person = new PersonAnalysis { Visible = true }
            }
        }, AspNetDefaults);

        var readBack = JsonSerializer.Deserialize<AlertResult>(onTheWire, AspNetDefaults);

        Assert.NotNull(readBack?.Analysis);
        Assert.True(readBack!.Analysis!.Vehicle.Visible, $"vehicle.visible did not survive the round trip: {onTheWire}");
        Assert.True(readBack.Analysis.Person.Visible, $"person.visible did not survive the round trip: {onTheWire}");
        Assert.True(AlertNotificationPolicy.HasVisibleSubject(readBack.Analysis));
    }

    // Fail-open, the rule the whole design rests on: anything other than the AI looking and
    // finding nothing has to leave the footage visible.
    [Theory]
    [InlineData("failed")]
    [InlineData("error")]
    public async Task AnAnalysisThatNeverCompleted_LeavesTheTriggerVisible(string status)
    {
        using var store = NewStore(out var root);
        try
        {
            var trigger = await store.AddAsync(
                Guid.NewGuid(), "snap.jpg", 0.2, null, TriggerConfirmation.Pending, TestContext.Current.CancellationToken);

            var service = NewService(
                store, new AlertResult { Id = Guid.NewGuid(), Status = status }, out var settingsPath);
            try
            {
                await service.ResolveAsync(
                    trigger.Id, new AlertForwardResult(Accepted: true, AlertId: Guid.NewGuid()),
                    TestContext.Current.CancellationToken);

                var stored = await ((ITriggerEventStore)store).GetAsync(trigger.Id, TestContext.Current.CancellationToken);
                Assert.Equal(TriggerConfirmation.Confirmed, stored!.Confirmation);
            }
            finally
            {
                File.Delete(settingsPath);
            }
        }
        finally
        {
            TempDirectories.Delete(root);
        }
    }

    // A completed analysis carrying no analysis object at all is not the AI saying "nothing
    // there" - it is the AI not having answered. Must stay visible.
    [Fact]
    public async Task ACompletedResultWithNoAnalysis_LeavesTheTriggerVisible()
    {
        using var store = NewStore(out var root);
        try
        {
            var trigger = await store.AddAsync(
                Guid.NewGuid(), "snap.jpg", 0.2, null, TriggerConfirmation.Pending, TestContext.Current.CancellationToken);

            var service = NewService(
                store, new AlertResult { Id = Guid.NewGuid(), Status = "completed", Analysis = null }, out var settingsPath);
            try
            {
                await service.ResolveAsync(
                    trigger.Id, new AlertForwardResult(Accepted: true, AlertId: Guid.NewGuid()),
                    TestContext.Current.CancellationToken);

                var stored = await ((ITriggerEventStore)store).GetAsync(trigger.Id, TestContext.Current.CancellationToken);
                Assert.Equal(TriggerConfirmation.Confirmed, stored!.Confirmation);
            }
            finally
            {
                File.Delete(settingsPath);
            }
        }
        finally
        {
            TempDirectories.Delete(root);
        }
    }

    // An alert the pipeline refused never gets an id to follow, so no verdict is ever coming.
    // Leaving it Pending would hide that footage permanently.
    [Fact]
    public async Task AnAlertThePipelineRefused_LeavesTheTriggerVisible()
    {
        using var store = NewStore(out var root);
        try
        {
            var trigger = await store.AddAsync(
                Guid.NewGuid(), "snap.jpg", 0.2, null, TriggerConfirmation.Pending, TestContext.Current.CancellationToken);

            var service = NewService(store, whatTheMainAppReturns: null, out var settingsPath);
            try
            {
                await service.ResolveAsync(
                    trigger.Id, AlertForwardResult.Refused, TestContext.Current.CancellationToken);

                var stored = await ((ITriggerEventStore)store).GetAsync(trigger.Id, TestContext.Current.CancellationToken);
                Assert.Equal(TriggerConfirmation.Confirmed, stored!.Confirmation);
            }
            finally
            {
                File.Delete(settingsPath);
            }
        }
        finally
        {
            TempDirectories.Delete(root);
        }
    }


    // --- The clip a rejected trigger leaves behind ---------------------------------------------
    //
    // Rejected motion is branches, rain, and moths on the illuminator, and the video of it is ~19 MB
    // against a snapshot's few tens of KB - so with the AI saying there was nothing there, the clip
    // goes and the still stays. These drive the whole path: verdict, store, and the unlink itself.

    private static async Task<(TriggerEvent Trigger, string ImagePath, string ClipPath)> AddWithFilesAsync(
        SqliteTriggerStore store, Guid cameraId, string root, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(root);
        var imagePath = Path.Combine(root, $"{Guid.NewGuid():N}.jpg");
        var clipPath = Path.Combine(root, $"{Guid.NewGuid():N}.mp4");
        await File.WriteAllTextAsync(imagePath, "snapshot", cancellationToken);
        await File.WriteAllTextAsync(clipPath, "video", cancellationToken);

        var trigger = await store.AddAsync(
            cameraId, imagePath, 0.2, null, TriggerConfirmation.Pending, cancellationToken);
        await store.SetClipPathAsync(trigger.Id, clipPath, cancellationToken);

        return (trigger, imagePath, clipPath);
    }

    private static AlertResult Empty(Guid id) => new()
    {
        Id = id,
        Status = "completed",
        Analysis = new VisionAnalysis { Summary = "Branches moving in the wind." }
    };

    private static AlertResult Vehicle(Guid id) => new()
    {
        Id = id,
        Status = "completed",
        Analysis = new VisionAnalysis
        {
            Summary = "A truck is in the drive.",
            Vehicle = new VehicleAnalysis { Visible = true }
        }
    };

    [Fact]
    public async Task AnEmptyFrame_DeletesTheClip_AndKeepsTheSnapshot()
    {
        using var store = NewStore(out var root);
        try
        {
            var ct = TestContext.Current.CancellationToken;
            var (trigger, imagePath, clipPath) = await AddWithFilesAsync(store, Guid.NewGuid(), root, ct);
            var completed = Empty(Guid.NewGuid());

            var service = NewService(store, completed, out var settingsPath);
            try
            {
                await service.ResolveAsync(trigger.Id, new AlertForwardResult(true, completed.Id), ct);

                Assert.False(File.Exists(clipPath));
                Assert.True(File.Exists(imagePath));

                // The row survives with its picture - "show hidden" still has something to render,
                // and a row left pointing at the deleted file would render as a dead video link.
                var stored = await ((ITriggerEventStore)store).GetAsync(trigger.Id, ct);
                Assert.Equal(TriggerConfirmation.Rejected, stored!.Confirmation);
                Assert.Equal(imagePath, stored.ImagePath);
                Assert.Null(stored.ClipPath);
            }
            finally
            {
                File.Delete(settingsPath);
            }
        }
        finally
        {
            TempDirectories.Delete(root);
        }
    }

    [Fact]
    public async Task AVehicleInFrame_KeepsTheClip()
    {
        using var store = NewStore(out var root);
        try
        {
            var ct = TestContext.Current.CancellationToken;
            var (trigger, _, clipPath) = await AddWithFilesAsync(store, Guid.NewGuid(), root, ct);
            var completed = Vehicle(Guid.NewGuid());

            var service = NewService(store, completed, out var settingsPath);
            try
            {
                await service.ResolveAsync(trigger.Id, new AlertForwardResult(true, completed.Id), ct);

                Assert.True(File.Exists(clipPath));

                var stored = await ((ITriggerEventStore)store).GetAsync(trigger.Id, ct);
                Assert.Equal(clipPath, stored!.ClipPath);
            }
            finally
            {
                File.Delete(settingsPath);
            }
        }
        finally
        {
            TempDirectories.Delete(root);
        }
    }

    [Fact]
    public async Task WithTheSettingOff_ARejectedTriggerKeepsItsClip()
    {
        using var store = NewStore(out var root);
        try
        {
            var ct = TestContext.Current.CancellationToken;
            var (trigger, _, clipPath) = await AddWithFilesAsync(store, Guid.NewGuid(), root, ct);
            var completed = Empty(Guid.NewGuid());

            var service = NewService(store, completed, out var settingsPath, discardRejectedClips: false);
            try
            {
                await service.ResolveAsync(trigger.Id, new AlertForwardResult(true, completed.Id), ct);

                var stored = await ((ITriggerEventStore)store).GetAsync(trigger.Id, ct);
                Assert.Equal(TriggerConfirmation.Rejected, stored!.Confirmation);

                // Still hidden from the tab, still on disk - the old behaviour, where the clip is
                // pruned days later by UnverifiedMotionRetentionDays instead.
                Assert.True(File.Exists(clipPath));
                Assert.Equal(clipPath, stored.ClipPath);
            }
            finally
            {
                File.Delete(settingsPath);
            }
        }
        finally
        {
            TempDirectories.Delete(root);
        }
    }

    // The AI never actually looked - the analysis errored, or the model was unreachable. That is an
    // infrastructure problem, not a judgement that the driveway was empty, and it must not cost
    // anyone their footage. Same instinct as the trigger staying visible on this path.
    [Fact]
    public async Task AnAnalysisThatFailed_KeepsTheClip()
    {
        using var store = NewStore(out var root);
        try
        {
            var ct = TestContext.Current.CancellationToken;
            var (trigger, _, clipPath) = await AddWithFilesAsync(store, Guid.NewGuid(), root, ct);
            var failed = new AlertResult { Id = Guid.NewGuid(), Status = "failed" };

            var service = NewService(store, failed, out var settingsPath);
            try
            {
                await service.ResolveAsync(trigger.Id, new AlertForwardResult(true, failed.Id), ct);

                var stored = await ((ITriggerEventStore)store).GetAsync(trigger.Id, ct);
                Assert.Equal(TriggerConfirmation.Confirmed, stored!.Confirmation);
                Assert.True(File.Exists(clipPath));
            }
            finally
            {
                File.Delete(settingsPath);
            }
        }
        finally
        {
            TempDirectories.Delete(root);
        }
    }

    // A clip covers a span, so it is routinely shared with a trigger the AI confirmed. That clip is
    // footage of something real and is kept whole, even though this trigger was rejected.
    [Fact]
    public async Task ARejectedTriggerSharingItsClipWithAConfirmedOne_KeepsTheClip()
    {
        using var store = NewStore(out var root);
        try
        {
            var ct = TestContext.Current.CancellationToken;
            var cameraId = Guid.NewGuid();
            var (rejected, _, clipPath) = await AddWithFilesAsync(store, cameraId, root, ct);

            var neighbour = await store.AddAsync(cameraId, "snap.jpg", 0.2, null, TriggerConfirmation.Confirmed, ct);
            await store.SetClipPathAsync(neighbour.Id, clipPath, ct);

            var completed = Empty(Guid.NewGuid());
            var service = NewService(store, completed, out var settingsPath);
            try
            {
                await service.ResolveAsync(rejected.Id, new AlertForwardResult(true, completed.Id), ct);

                Assert.True(File.Exists(clipPath));
                var stored = await ((ITriggerEventStore)store).GetAsync(neighbour.Id, ct);
                Assert.Equal(clipPath, stored!.ClipPath);
            }
            finally
            {
                File.Delete(settingsPath);
            }
        }
        finally
        {
            TempDirectories.Delete(root);
        }
    }

    // The normal order on a real camera: the analysis answers first, and the clip only exists once
    // the post-roll has run out. TriggerZoneMonitor.BuildClipAsync asks again at that point, and
    // this is that second ask.
    [Fact]
    public async Task WhenTheVerdictBeatsTheClip_TheLaterAskCollectsIt()
    {
        using var store = NewStore(out var root);
        try
        {
            var ct = TestContext.Current.CancellationToken;
            Directory.CreateDirectory(root);
            var trigger = await store.AddAsync(
                Guid.NewGuid(), "snap.jpg", 0.2, null, TriggerConfirmation.Pending, ct);

            var completed = Empty(Guid.NewGuid());
            var service = NewService(store, completed, out var settingsPath);
            try
            {
                // Rejected while the clip is still being cut - nothing to delete yet.
                await service.ResolveAsync(trigger.Id, new AlertForwardResult(true, completed.Id), ct);

                var clipPath = Path.Combine(root, "late.mp4");
                await File.WriteAllTextAsync(clipPath, "video", ct);
                await store.SetClipPathAsync(trigger.Id, clipPath, ct);

                await service.DiscardClipIfRejectedAsync(trigger.Id, ct);

                Assert.False(File.Exists(clipPath));
                Assert.Null((await ((ITriggerEventStore)store).GetAsync(trigger.Id, ct))!.ClipPath);
            }
            finally
            {
                File.Delete(settingsPath);
            }
        }
        finally
        {
            TempDirectories.Delete(root);
        }
    }

    // Stands in for the main app's /api/results/{id}, answering exactly as it would.
    private sealed class StubMainApp(AlertResult? result) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (result is null)
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    JsonSerializer.Serialize(result, AspNetDefaults), Encoding.UTF8, "application/json")
            });
        }
    }

    private sealed class SingleHandlerFactory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
    }
}
