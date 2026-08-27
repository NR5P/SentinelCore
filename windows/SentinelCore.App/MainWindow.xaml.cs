using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using SentinelCore.App.Design;
using SentinelCore.App.Views;
using SentinelCore.Client;
using SentinelCore.Client.Models;

namespace SentinelCore.App;

/// <summary>
/// The shell: which section is on screen, whether the app is showing setup, a login, or the app
/// proper, and the periodic refresh that keeps the feed current. A port of RootView.swift.
/// </summary>
public partial class MainWindow : Window, INavigator
{
    private readonly AppModel _model;
    private readonly TrayNotifier _tray;
    private readonly DispatcherTimer _refreshTimer;

    // What is stacked on top of the current section. Only ever one deep in practice - an event, or
    // one camera - but a stack keeps "where does Back go" a question with a single answer.
    private readonly Stack<(FrameworkElement View, string Title)> _stack = new();

    private EventsView? _events;
    private TimelineView? _timeline;
    private LiveView? _live;
    private DeviceHubView? _devices;
    private SettingsView? _settings;

    public MainWindow(AppModel model)
    {
        _model = model;
        InitializeComponent();

        SetupView.Attach(model);
        LoginView.Attach(model);

        _tray = new TrayNotifier(this, model);
        _model.NewNotifiedEvents += OnNewNotifiedEvents;
        _model.PropertyChanged += OnModelPropertyChanged;

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

        // The Device Hub is off by default and only appears once it's switched on server-side, so
        // the list is filtered rather than fixed.
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
            AppSection.Devices => _devices ??= NewDeviceHubView(),
            _ => _settings ??= new SettingsView(_model),
        };
    }

    private LiveView NewLiveView()
    {
        var view = new LiveView(_model, this);
        view.SetActive(true);
        return view;
    }

    private DeviceHubView NewDeviceHubView()
    {
        var view = new DeviceHubView(_model);
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
                break;
            case nameof(AppModel.DeviceHubEnabled):
                BuildSections();
                break;
            case nameof(AppModel.CompatibilityAdvisory):
            case nameof(AppModel.IsUsingRemote):
            case nameof(AppModel.UsingHttpFallback):
                ApplyBanner();
                break;
        }
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
        VersionBanner.Visibility = advisory is null ? Visibility.Collapsed : Visibility.Visible;
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
            Hide();
            return;
        }

        _refreshTimer.Stop();
        _model.NewNotifiedEvents -= OnNewNotifiedEvents;
        _model.PropertyChanged -= OnModelPropertyChanged;
        _live?.SetActive(false);
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
