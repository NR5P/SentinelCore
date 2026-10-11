using System.IO;
using System.Windows;
using SentinelCore.App.Views;
using SentinelCore.Client;
using SentinelCore.Client.Models;
using Xunit;

namespace SentinelCore.App.Tests;

/// <summary>
/// Builds every window and section in the app, once, and fails if any of them throws.
/// </summary>
/// <remarks>
/// XAML is resolved at runtime, not at compile time: a mistyped StaticResource key, a binding to a
/// converter that was never registered, a template referring to a control that has been renamed -
/// none of it is a build error. It is an exception the first time somebody opens that one dialog,
/// which on a screen like Trigger Zones might be weeks after the change that broke it.
///
/// This is deliberately a smoke test and not a UI test. It does not click anything or assert on
/// layout; it only proves that each screen can be constructed against a model with nothing in it,
/// which is the state every one of them has to survive anyway - a window that appears before its
/// first request comes back.
/// </remarks>
[Collection("STA")]
public class WindowSmokeTests
{
    [Fact]
    public void Every_screen_can_be_built_against_an_empty_model()
    {
        var failures = StaRunner.Run(() =>
        {
            EnsureApplication();

            var model = NewModel();
            var camera = new Camera { Id = Guid.NewGuid(), Name = "Front Door", StreamUrl = "rtsp://x" };
            var device = new HubDevice { Id = Guid.NewGuid(), Name = "Porch light", Kind = "light" };
            var schedule = new Schedule { Id = Guid.NewGuid(), Name = "Gate at dusk", Kind = "solar" };
            var drive = new FamilyDrive { Id = "d1", Name = "USB drive", Kind = "folder", FolderPath = "/mnt/usb" };
            var library = new MediaLibrary { Id = "l1", Name = "Movies", Kind = "movies", Path = "/mnt/movies" };
            var computer = new MediaNetworkComputer
            {
                Id = "c1", Name = "Den PC", Host = "192.168.1.20", Username = "pat", HasPassword = true,
                Path = "smb://192.168.1.20",
            };
            var activity = new ActivityItem
            {
                Id = Guid.NewGuid().ToString(),
                Kind = "analyzed",
                Camera = "Front Door",
                Summary = "A courier left a parcel.",
            };

            var screens = new (string Name, Func<object> Build)[]
            {
                ("EventsView", () => new EventsView(model)),
                ("ActivityDetailView", () => new ActivityDetailView(model, activity)),
                ("LiveCameraView", () => new LiveCameraView(model, camera)),
                ("TimelineView", () => new TimelineView(model)),
                ("LiveView", () => new LiveView(model)),
                ("DeviceHubView", () => new DeviceHubView(model)),
                ("MoviesView", () => new MoviesView(model)),
                ("MovieDetailView", () => new MovieDetailView(model, null, "m1")),
                ("MoviePlayerView", () => new MoviePlayerView(model, NullNavigator.Instance, "m1", null)),
                ("FilesView", () => new FilesView(model)),
                ("SettingsView", () => new SettingsView(model)),
                ("ServerSetupView", () => new ServerSetupView()),
                ("LoginView", () => new LoginView()),

                ("CameraAdminWindow", () => new CameraAdminWindow(model)),
                ("CameraEditWindow (add)", () => new CameraEditWindow(model, null)),
                ("CameraEditWindow (edit)", () => new CameraEditWindow(model, camera)),
                ("CameraDiscoveryWindow", () => new CameraDiscoveryWindow(model)),
                ("TriggerZoneEditorWindow", () => new TriggerZoneEditorWindow(model, camera)),
                ("AccountAdminWindow", () => new AccountAdminWindow(model)),
                ("DashboardLoginWindow", () => new DashboardLoginWindow(model)),
                ("ChangePasswordWindow", () => new ChangePasswordWindow(model)),
                ("KnownFacesWindow", () => new KnownFacesWindow(model)),
                ("KnownVehiclesWindow", () => new KnownVehiclesWindow(model)),
                ("ScheduleListWindow", () => new ScheduleListWindow(model)),
                ("ScheduleEditWindow (add)", () => new ScheduleEditWindow(model, ScheduleMetadata.Empty, null)),
                ("ScheduleEditWindow (edit)", () => new ScheduleEditWindow(model, ScheduleMetadata.Empty, schedule)),
                ("ServerSettingsWindow", () => new ServerSettingsWindow(model)),
                ("PromptProfilesWindow", () => new PromptProfilesWindow(model)),
                ("HomeKitWindow", () => new HomeKitWindow(model)),
                ("BackupWindow", () => new BackupWindow(model)),
                ("LicenseWindow", () => new LicenseWindow(model)),
                ("HostRebootWindow", () => new HostRebootWindow(model)),
                ("DeviceEditWindow (add)", () => new DeviceEditWindow(model, null)),
                ("DeviceEditWindow (edit)", () => new DeviceEditWindow(model, device)),
                ("DeviceDiscoveryWindow", () => new DeviceDiscoveryWindow(model)),
                ("FilesDrivesWindow", () => new FilesDrivesWindow(model)),
                ("FilesDriveEditWindow (add)", () => new FilesDriveEditWindow(model, null)),
                ("FilesDriveEditWindow (edit)", () => new FilesDriveEditWindow(model, drive)),
                ("FilesTrashWindow", () => new FilesTrashWindow(model, isAdmin: true)),
                ("MediaLibrariesWindow", () => new MediaLibrariesWindow(model)),
                ("MediaLibraryEditWindow (add)", () => new MediaLibraryEditWindow(model, null)),
                ("MediaLibraryEditWindow (edit)", () => new MediaLibraryEditWindow(model, library)),
                ("MediaFolderPickerWindow", () => new MediaFolderPickerWindow(model)),
                ("MediaFolderPickerWindow (start path)", () => new MediaFolderPickerWindow(model, "smb://nas/Movies")),
                ("MediaNetworkSignInWindow (new)", () => new MediaNetworkSignInWindow(model, null, "den-pc")),
                ("MediaNetworkSignInWindow (saved)", () => new MediaNetworkSignInWindow(model, computer)),
                ("MediaDownloadsView", () => new MediaDownloadsView(model, NullNavigator.Instance, null)),
                ("MediaDownloadAddWindow (off)", () => new MediaDownloadAddWindow(model, null)),
                ("MediaDownloadAddWindow (no TMDB)", () => new MediaDownloadAddWindow(model, new MediaDownloadStatus
                {
                    CanDownload = true, TmdbConfigured = false, MovieLibrary = false, ShowLibrary = true,
                })),
                ("MediaDownloadSitesWindow", () => new MediaDownloadSitesWindow(model)),
                ("MediaDownloadAddWindow (VPN off)", () => new MediaDownloadAddWindow(model, DownloadStatus("stopped", "disabled", false))),
                ("MoviesView (every VPN light)", () =>
                {
                    var view = new MoviesView(model);
                    foreach (var (services, state, verified) in new[]
                    {
                        ("running", "connected", true),      // green
                        ("running", "connected", false),     // cyan, checking
                        ("starting", "connecting", false),   // cyan, connecting
                        ("stopped", "off", false),           // grey
                        ("external", "unknown", false),      // grey
                        ("stopped", "disabled", false),      // amber
                        ("failed", "down", false),           // red
                        ("notInstalled", "off", false),      // red
                    })
                    {
                        view.ApplyDownloadStatus(DownloadStatus(services, state, verified));
                    }
                    view.ApplyDownloadStatus(MediaDownloadStatus.Off);
                    return view;
                }),
                // Music & Podcasts. The player is made first: the mini bar, Now Playing and the
                // settings panel all hang off it (it starts no audio until something is played).
                ("PlayerController", () => SentinelCore.App.Audio.PlayerController.Shared = new SentinelCore.App.Audio.PlayerController(model, System.Windows.Threading.Dispatcher.CurrentDispatcher)),
                ("MiniPlayerBar", () => new SentinelCore.App.Views.Audio.MiniPlayerBar(SentinelCore.App.Audio.PlayerController.Shared!, NullNavigator.Instance)),
                ("NowPlayingView", () => new SentinelCore.App.Views.Audio.NowPlayingView(SentinelCore.App.Audio.PlayerController.Shared!, NullNavigator.Instance)),
                ("MusicView", () => new SentinelCore.App.Views.Audio.MusicView(model, NullNavigator.Instance)),
                ("AlbumView", () => new SentinelCore.App.Views.Audio.AlbumView(model, NullNavigator.Instance, "a1")),
                ("ArtistView", () => new SentinelCore.App.Views.Audio.ArtistView(model, NullNavigator.Instance, "a1")),
                ("PlaylistView", () => new SentinelCore.App.Views.Audio.PlaylistView(model, NullNavigator.Instance, "p1")),
                ("MusicTrackListView", () => new SentinelCore.App.Views.Audio.MusicTrackListView(model, NullNavigator.Instance, "Rock", genre: "Rock")),
                ("MusicDownloadsView", () => new SentinelCore.App.Views.Audio.MusicDownloadsView(model, NullNavigator.Instance)),
                ("AudioStatsView", () => new SentinelCore.App.Views.Audio.AudioStatsView(model, NullNavigator.Instance)),
                ("PodcastsView", () => new SentinelCore.App.Views.Audio.PodcastsView(model, NullNavigator.Instance)),
                ("PodcastShowView", () => new SentinelCore.App.Views.Audio.PodcastShowView(model, NullNavigator.Instance, "s1")),
                ("PodcastEpisodeView", () => new SentinelCore.App.Views.Audio.PodcastEpisodeView(model, NullNavigator.Instance, "e1")),
                ("PodcastPreviewView", () => new SentinelCore.App.Views.Audio.PodcastPreviewView(model, NullNavigator.Instance, "https://example.com/feed.xml", null)),
                ("PodcastClipsView", () => new SentinelCore.App.Views.Audio.PodcastClipsView(model, NullNavigator.Instance)),
                ("AudioSettingsPanel", () => new SentinelCore.App.Views.Audio.AudioSettingsPanel(model)),
                ("MusicLibrariesWindow", () => new SentinelCore.App.Views.Audio.MusicLibrariesWindow(model)),
                ("PlaylistEditorWindow (new)", () => new SentinelCore.App.Views.Audio.PlaylistEditorWindow(model, null, smart: false)),
                ("PlaylistEditorWindow (smart)", () => new SentinelCore.App.Views.Audio.PlaylistEditorWindow(model, null, smart: true)),
                ("MusicAddWindow", () => new SentinelCore.App.Views.Audio.MusicAddWindow(model, MusicDownloadStatus.Off)),
                ("ListenBrainzWindow", () => new SentinelCore.App.Views.Audio.ListenBrainzWindow(model)),
                ("SpeakerPickerWindow", () => new SentinelCore.App.Views.Audio.SpeakerPickerWindow(model)),
                ("WakeAlarmsWindow", () => new SentinelCore.App.Views.Audio.WakeAlarmsWindow(model)),
                ("WakeAlarmEditorWindow", () => new SentinelCore.App.Views.Audio.WakeAlarmEditorWindow(model, null)),
                ("PodcastSettingsWindow", () => new SentinelCore.App.Views.Audio.PodcastSettingsWindow(model, new Podcast { Id = "s1", Title = "Show" })),
                ("PodcastClipWindow", () => new SentinelCore.App.Views.Audio.PodcastClipWindow(model, new PodcastEpisode { Id = "e1", Title = "Ep" }, 120)),
                ("MediaFolderPickerWindow (audio)", () => new MediaFolderPickerWindow(model, null, SentinelCore.Client.Networking.SentinelClient.AudioBase)),
                ("TextPromptWindow", () => new TextPromptWindow("Title", "Prompt?")),
                ("ListPickerWindow", () => new ListPickerWindow("Pick", new[] { "One", "Two" })),
            };

            var broken = new List<string>();
            foreach (var (name, build) in screens)
            {
                try
                {
                    var screen = build();
                    Assert.NotNull(screen);
                    if (screen is Window window) window.Close();
                }
                catch (Exception error)
                {
                    broken.Add($"{name}: {error.GetType().Name}: {error.Message}");
                }
            }

            SentinelCore.App.Audio.PlayerController.Shared?.Dispose();
            SentinelCore.App.Audio.PlayerController.Shared = null;
            model.Dispose();
            return broken;
        });

        Assert.True(failures.Count == 0, "Screens that could not be built:\n  " + string.Join("\n  ", failures));
    }

    private static MediaDownloadStatus DownloadStatus(string services, string vpnState, bool verified) => new()
    {
        CanDownload = true,
        Status = new MediaDownloadServiceStatus
        {
            Services = services,
            Vpn = new MediaDownloadVpn { State = vpnState, Verified = verified, Country = "SE", Location = "Stockholm, SE" },
        },
    };

    private static AppModel NewModel()
    {
        var directory = Path.Combine(Path.GetTempPath(), "sentinel-smoke", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var model = new AppModel(
            new PreferenceStore(Path.Combine(directory, "settings.json")),
            new Client.Networking.CredentialStore(Path.Combine(directory, "credentials")));
        // Pointed at a port nothing listens on: the background loads each screen kicks off fail
        // quickly and harmlessly, which is the same thing they do when the server is off.
        model.ServerUrl = "http://127.0.0.1:9";
        return model;
    }

    /// <summary>
    /// Loads the real App.xaml resources, so a StaticResource lookup in any window resolves against
    /// exactly the dictionary the shipping app uses rather than a stand-in that could disagree.
    /// </summary>
    private static void EnsureApplication()
    {
        if (Application.Current is not null) return;
        var app = new SentinelCore.App.App();
        app.InitializeComponent();
    }
}

/// <summary>Runs a delegate on a fresh STA thread, which WPF requires of anything it constructs.</summary>
internal static class StaRunner
{
    public static T Run<T>(Func<T> work)
    {
        T result = default!;
        Exception? failure = null;

        var thread = new Thread(() =>
        {
            try
            {
                result = work();
            }
            catch (Exception error)
            {
                failure = error;
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.IsBackground = true;
        thread.Start();

        // Generous, but bounded: a hang here is a real defect and should fail rather than wedge the
        // whole test run.
        if (!thread.Join(TimeSpan.FromSeconds(60)))
        {
            throw new TimeoutException("Building the app's screens did not finish within 60 seconds.");
        }

        if (failure is not null) throw failure;
        return result;
    }
}

[CollectionDefinition("STA", DisableParallelization = true)]
public class StaCollection
{
}
