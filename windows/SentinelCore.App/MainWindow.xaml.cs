using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using SentinelCore.App.Design;
using SentinelCore.App.Organizer;
using SentinelCore.App.Views;
using SentinelCore.Client;
using SentinelCore.Client.Models;
using SentinelCore.Client.Organizer;

namespace SentinelCore.App;

/// <summary>
/// The shell: which section is on screen, whether the app is showing setup, a login, or the app
/// proper, and the periodic refresh that keeps the feed current. A port of RootView.swift.
/// </summary>
public partial class MainWindow : Window, INavigator
{
    private readonly AppModel _model;
    private readonly OrganizerSession? _organizer;
    private readonly OrganizerAlerts? _organizerAlerts;
    private readonly TrayNotifier _tray;
    private readonly DispatcherTimer _refreshTimer;

    // What is stacked on top of the current section. Only ever one deep in practice - an event, or
    // one camera - but a stack keeps "where does Back go" a question with a single answer.
    private readonly Stack<(FrameworkElement View, string Title)> _stack = new();

    private EventsView? _events;
    private TimelineView? _timeline;
    private LiveView? _live;
    private MoviesView? _movies;
    private Views.Audio.MusicView? _music;
    private Views.Audio.PodcastsView? _podcasts;
    private readonly Audio.PlayerController _player;
    private WeatherView? _weather;
    private DeviceHubView? _devices;
    private AlarmView? _alarm;
    private FilesView? _files;
    private OrganizerView? _organizerView;
    private SettingsView? _settings;

    public MainWindow(AppModel model, OrganizerSession? organizer = null)
    {
        _model = model;
        _organizer = organizer;
        InitializeComponent();

        SetupView.Attach(model);
        LoginView.Attach(model);

        // One player for Music and Podcasts, alive for as long as the app is (it keeps playing
        // with the window hidden in the tray), with its mini bar under every section.
        _player = new Audio.PlayerController(model, Dispatcher);
        Audio.PlayerController.Shared = _player;
        PlayerBarHost.Content = new Views.Audio.MiniPlayerBar(_player, this);

        _tray = new TrayNotifier(this, model);
        _model.NewNotifiedEvents += OnNewNotifiedEvents;
        _model.PropertyChanged += OnModelPropertyChanged;

        // Alerts come from this PC's own copy, so they fire with the server down - and whether or
        // not the section is showing. A reminder set on the phone still alerts here.
        if (organizer is not null)
        {
            organizer.EnabledChanged += OnOrganizerEnabledChanged;
            _organizerAlerts = new OrganizerAlerts(organizer.Engine,
                alerts => _tray.NotifyOrganizer(
                    alerts.Count == 1 ? alerts[0].Title : $"{alerts.Count} reminders and events",
                    alerts.Count == 1 ? alerts[0].Body : string.Join("\n", alerts.Take(4).Select(alert => alert.Title)),
                    alerts[0].ItemId));
        }

        // The feed refreshes itself every 30 seconds, matching the Apple client. A monitoring
        // window left open on a desk has to be current without anyone touching it.
        _refreshTimer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromSeconds(30),
        };
        _refreshTimer.Tick += async (_, _) => await RefreshQuietlyAsync();
        _refreshTimer.Start();

        BuildSections();
        ApplyState();

        Loaded += async (_, _) => await _model.ConnectAsync(resetNetworkSession: true);
    }

    private sealed record SectionItem(AppSection Section, string Title, string Glyph);

    /// <summary>
    /// "Feed" and "Triggers" used to be separate sections - one for AI-analysed alerts, one for raw
    /// motion - which put a camera that forwards to AI analysis on both, once with the description
    /// and once with the clip. They are now one Events section over the server's merged list.
    /// </summary>
    public enum AppSection
    {
        Events,
        Timeline,
        Live,
        Movies,
        Music,
        Podcasts,
        Weather,
        Alarm,
        Organizer,
        Files,
        Devices,
        Settings,
    }

    private void BuildSections()
    {
        var sections = new List<SectionItem>
        {
            new(AppSection.Events, "Events", ""),
            new(AppSection.Timeline, "Timeline", ""),
            new(AppSection.Live, "Live", ""),
        };

        // Movies & TV is switched on server-side, like the Device Hub, and only exists while it is.
        if (_model.MoviesEnabled) sections.Add(new SectionItem(AppSection.Movies, "Movies", ""));

        // Music and Podcasts each exist only while the server has them on (/api/audio/enabled).
        if (_model.MusicEnabled) sections.Add(new SectionItem(AppSection.Music, "Music", ""));
        if (_model.PodcastsEnabled) sections.Add(new SectionItem(AppSection.Podcasts, "Podcasts", ""));

        // Always there, like the dashboard's tab: with no location set it says how to set one.
        sections.Add(new SectionItem(AppSection.Weather, "Weather", "\uE753"));

        // The alarm is on by default but can be switched off server-side.
        if (_model.AlarmEnabled) sections.Add(new SectionItem(AppSection.Alarm, "Alarm", "\uE72E"));

        // The Organizer is on by default but can be switched off server-side; what the server said
        // last is remembered, so it's there offline too.
        if (_organizer is { Enabled: true }) sections.Add(new SectionItem(AppSection.Organizer, "Organizer", "\uE8BF"));

        // Family Files and the Device Hub are off by default and only appear once switched on
        // server-side, so the list is filtered rather than fixed.
        if (_model.FamilyFilesEnabled) sections.Add(new SectionItem(AppSection.Files, "Files", ""));
        if (_model.DeviceHubEnabled) sections.Add(new SectionItem(AppSection.Devices, "Device Hub", ""));
        sections.Add(new SectionItem(AppSection.Settings, "Settings", ""));

        var previous = (SectionList.SelectedItem as SectionItem)?.Section ?? AppSection.Events;
        SectionList.ItemsSource = sections;
        SectionList.SelectedItem = sections.FirstOrDefault(item => item.Section == previous) ?? sections[0];
    }

    private void OnSectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (SectionList.SelectedItem is not SectionItem item) return;

        // Leaving a section abandons whatever was pushed on top of it: a camera detail belonging to
        // Live has no meaning once Settings is showing.
        ClearStack();

        // The Live grid and the Device Hub notice for themselves when they stop being visible, so
        // there is nothing to switch off here.
        SectionHost.Content = item.Section switch
        {
            AppSection.Events => _events ??= new EventsView(_model, this),
            AppSection.Timeline => _timeline ??= new TimelineView(_model),
            AppSection.Live => _live ??= NewLiveView(),
            AppSection.Movies => _movies ??= new MoviesView(_model, this),
            AppSection.Music => _music ??= new Views.Audio.MusicView(_model, this),
            AppSection.Podcasts => _podcasts ??= new Views.Audio.PodcastsView(_model, this),
            AppSection.Weather => _weather ??= NewWeatherView(),
            AppSection.Devices => _devices ??= NewDeviceHubView(),
            AppSection.Alarm => _alarm ??= NewAlarmView(),
            AppSection.Files => _files ??= NewFilesView(),
            AppSection.Organizer when _organizer is not null => _organizerView ??= new OrganizerView(_organizer),
            _ => _settings ??= new SettingsView(_model, _organizer),
        };
    }

    private LiveView NewLiveView()
    {
        var view = new LiveView(_model, this);
        view.SetActive(true);
        return view;
    }

    private WeatherView NewWeatherView()
    {
        var view = new WeatherView(_model);
        view.SetActive(true);
        return view;
    }

    private AlarmView NewAlarmView()
    {
        var view = new AlarmView(_model);
        view.SetActive(true);
        return view;
    }

    private DeviceHubView NewDeviceHubView()
    {
        var view = new DeviceHubView(_model);
        view.SetActive(true);
        return view;
    }

    private FilesView NewFilesView()
    {
        var view = new FilesView(_model);
        view.SetActive(true);
        return view;
    }

    private void OnModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(AppModel.ConnectionState):
            case nameof(AppModel.IsConfigured):
            case nameof(AppModel.AuthStatus):
                ApplyState();
                ResumeDeviceCamera();
                break;
            case nameof(AppModel.DeviceHubEnabled):
            case nameof(AppModel.MoviesEnabled):
            case nameof(AppModel.MusicEnabled):
            case nameof(AppModel.PodcastsEnabled):
            case nameof(AppModel.AlarmEnabled):
            case nameof(AppModel.FamilyFilesEnabled):
                BuildSections();
                break;
            case nameof(AppModel.CompatibilityAdvisory):
            case nameof(AppModel.IsUsingRemote):
            case nameof(AppModel.UsingHttpFallback):
                ApplyBanner();
                break;
        }
    }

    // If this PC was streaming as a camera when the app last closed, it goes back to being one as
    // soon as the server is reachable - the window opens on its own, so it is obvious that it did.
    // See DeviceCameraWindow.
    private bool _deviceCameraResumed;

    private void ResumeDeviceCamera()
    {
        if (_deviceCameraResumed || _model.ConnectionState != ConnectionState.Online || !_model.IsAdmin) return;
        if (Views.DeviceCameraWindow.StoredCameraId is not { } cameraId) return;
        _deviceCameraResumed = true;
        Dispatcher.BeginInvoke(() => Views.DeviceCameraWindow.Open(_model, cameraId));
    }

    private void ApplyState()
    {
        SetupState.Visibility = !_model.IsConfigured ? Visibility.Visible : Visibility.Collapsed;
        LoginState.Visibility = _model.IsConfigured && _model.RequiresLogin ? Visibility.Visible : Visibility.Collapsed;
        MainState.Visibility = _model.IsConfigured && !_model.RequiresLogin ? Visibility.Visible : Visibility.Collapsed;

        StatusText.Text = _model.ConnectionStateLabel;
        StatusDot.Fill = _model.ConnectionState switch
        {
            ConnectionState.Online => (Brush)FindResource("Green"),
            ConnectionState.Checking or ConnectionState.AuthenticationRequired => (Brush)FindResource("Amber"),
            ConnectionState.Offline => (Brush)FindResource("Red"),
            _ => (Brush)FindResource("SecondaryText"),
        };

        ApplyBanner();
    }

    private void ApplyBanner()
    {
        // Advisory, never blocking - the app works normally behind it. Amber rather than red for
        // that reason: this is "you'll want to update the server soon", not "something is broken".
        var advisory = _model.CompatibilityAdvisory;
        VersionBanner.Visibility = advisory is null || _immersive ? Visibility.Collapsed : Visibility.Visible;
        VersionBannerText.Text = advisory ?? "";

        // Which of the two addresses is carrying this session, and whether encryption was dropped
        // to get there. Neither should ever be something you have to go digging for.
        var route = _model switch
        {
            { UsingHttpFallback: true } => "via Local, plain HTTP",
            { IsUsingRemote: true } => "via Remote",
            { HasRemoteServerUrl: true } => "via Local",
            _ => null,
        };
        RouteText.Text = route ?? "";
        RouteText.Visibility = route is null ? Visibility.Collapsed : Visibility.Visible;
    }

    /// <summary>
    /// The background refresh. Deliberately silent about failures: a blip on the Wi-Fi should leave
    /// the last good list on screen and the badge saying Offline, not a dialog on top of it.
    /// </summary>
    private async Task RefreshQuietlyAsync()
    {
        if (!_model.IsConfigured || _model.RequiresLogin) return;
        try
        {
            await _model.ConnectAsync();
        }
        catch (Exception error)
        {
            _model.Log($"Background refresh failed: {error.Message}", DiagnosticLevel.Notice);
        }
    }

    private void OnNewNotifiedEvents(IReadOnlyList<ActivityItem> events) => _tray.Notify(events);

    private void OnOrganizerEnabledChanged() => Dispatcher.BeginInvoke(BuildSections);

    /// <summary>Opens the Organizer on one item - used by an alert notification.</summary>
    public void ShowOrganizerItem(string itemId)
    {
        if (_organizer is null) return;
        if (SectionList.ItemsSource is IEnumerable<SectionItem> sections
            && sections.FirstOrDefault(section => section.Section == AppSection.Organizer) is { } organizer)
        {
            SectionList.SelectedItem = organizer;
            _organizerView?.Reveal(itemId);
        }
    }

    // ---------------------------------------------------------------- Navigation

    /// <summary>
    /// Shows a detail on top of the current section, with a way back. The section itself stays
    /// built and keeps its scroll position and filters, so Back returns to exactly what was there.
    /// </summary>
    public void Push(FrameworkElement view, string title)
    {
        if (SectionHost.Content is FrameworkElement current)
        {
            _stack.Push((current, BackBarTitle.Text));
        }
        SectionHost.Content = view;
        BackBarTitle.Text = title;
        BackBar.Visibility = Visibility.Visible;
    }

    public void Pop()
    {
        if (_stack.Count == 0) return;

        // A live camera or a clip keeps a decoder and a socket alive; going back has to release
        // them rather than leave them running behind a screen nobody is looking at.
        if (SectionHost.Content is IDisposable disposable) disposable.Dispose();
        if (SectionHost.Content is LiveCameraView camera) camera.Stop();

        var (view, title) = _stack.Pop();
        SectionHost.Content = view;
        BackBarTitle.Text = title;
        BackBar.Visibility = _stack.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
    }

    private void ClearStack()
    {
        while (_stack.Count > 0) Pop();
        BackBar.Visibility = Visibility.Collapsed;
        BackBarTitle.Text = "";
    }

    private void OnBackClicked(object sender, RoutedEventArgs e) => Pop();

    // Whether the view on top has the whole window (the movie player), and, while it is also full
    // screen, how the window looked before so it can be put back exactly.
    private bool _immersive;
    private (WindowStyle Style, WindowState State, ResizeMode Resize)? _windowed;

    public void SetImmersive(bool immersive, bool fullScreen = false)
    {
        _immersive = immersive;
        Sidebar.Visibility = immersive ? Visibility.Collapsed : Visibility.Visible;
        SidebarColumn.Width = immersive ? new GridLength(0) : new GridLength(216);
        BackBar.Visibility = !immersive && _stack.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        ApplyBanner();

        if (immersive && fullScreen)
        {
            if (_windowed is not null) return;
            _windowed = (WindowStyle, WindowState, ResizeMode);
            // Normal first: a window that is already maximised does not re-cover the taskbar when
            // only its style changes.
            WindowState = WindowState.Normal;
            WindowStyle = WindowStyle.None;
            ResizeMode = ResizeMode.NoResize;
            WindowState = WindowState.Maximized;
        }
        else if (_windowed is { } saved)
        {
            _windowed = null;
            WindowStyle = saved.Style;
            ResizeMode = saved.Resize;
            WindowState = saved.State;
        }
    }

    protected override void OnPreviewKeyDown(System.Windows.Input.KeyEventArgs e)
    {
        // Escape goes back, which is what every other detail-over-list app on this desktop does.
        if (e.Key == System.Windows.Input.Key.Escape && _stack.Count > 0)
        {
            Pop();
            e.Handled = true;
            return;
        }
        base.OnPreviewKeyDown(e);
    }

    /// <summary>Opens the Events section and selects one alert - used by the tray notification.</summary>
    public void ShowEvent(ActivityItem item)
    {
        if (SectionList.ItemsSource is IEnumerable<SectionItem> sections)
        {
            SectionList.SelectedItem = sections.FirstOrDefault(section => section.Section == AppSection.Events);
        }
        _events?.Select(item);
    }

    /// <summary>
    /// Closing the window hides it; Exit from the tray is what actually quits.
    /// </summary>
    /// <remarks>
    /// The alerts this app raises depend on its own refresh still running, so ending the process on
    /// a window close meant the X button silently switched notifications off - and it also made the
    /// tray menu's "Open Sentinel Core" unreachable, since the icon could never outlive the window.
    /// The Apple app behaves the same way: closing the window leaves the app running.
    ///
    /// Hidden rather than minimised, so it leaves the taskbar and lives only in the tray. The live
    /// tiles are stopped on the way down - the grid is not visible and has no business holding a
    /// decoder and a socket per camera open behind a hidden window.
    /// </remarks>
    protected override void OnClosing(CancelEventArgs e)
    {
        if (!_exiting)
        {
            e.Cancel = true;
            _live?.SetActive(false);
            _weather?.SetActive(false);
            Hide();
            return;
        }

        _refreshTimer.Stop();
        _organizerAlerts?.Dispose();
        if (_organizer is not null) _organizer.EnabledChanged -= OnOrganizerEnabledChanged;
        _model.NewNotifiedEvents -= OnNewNotifiedEvents;
        _model.PropertyChanged -= OnModelPropertyChanged;
        _live?.SetActive(false);
        _player.Dispose();
        Audio.PlayerController.Shared = null;
        _tray.Dispose();
        base.OnClosing(e);
    }

    private bool _exiting;

    /// <summary>
    /// Quits for real, from the tray's Exit entry. Closes the window along the way so anything
    /// holding state gets its normal teardown rather than being shut down underneath.
    /// </summary>
    public void ExitApplication()
    {
        _exiting = true;
        Close();
        Application.Current.Shutdown();
    }
}
