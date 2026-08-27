using System.Drawing;
using System.Windows;
using System.Windows.Forms;
using SentinelCore.Client;
using SentinelCore.Client.Models;
using Application = System.Windows.Application;

namespace SentinelCore.App;

/// <summary>
/// The tray icon, and the notification this app raises when the server decides an alert is worth
/// notifying about.
/// </summary>
/// <remarks>
/// Windows has no APNs, and the server's push providers are Apple and Firebase - so unlike the
/// phone, this client cannot register for a push token. What a desktop client can do, and is
/// actually better placed to do, is raise a native notification the moment its own refresh brings
/// back a notified alert. See <see cref="AppModel.NewlyNotified"/> for why the first refresh of a
/// session only seeds the set rather than firing a notification per row of history.
///
/// A tray balloon rather than a Windows 10/11 toast on purpose: a real toast needs the app to be
/// packaged with an identity (MSIX or a registered AUMID with a Start shortcut), and this app is
/// meant to run from a folder. A balloon needs nothing and works on every Windows this app targets.
/// </remarks>
public sealed class TrayNotifier : IDisposable
{
    private readonly MainWindow _window;
    private readonly AppModel _model;
    private readonly NotifyIcon _icon;

    private ActivityItem? _lastNotified;

    public TrayNotifier(MainWindow window, AppModel model)
    {
        _window = window;
        _model = model;

        _icon = new NotifyIcon
        {
            Icon = LoadIcon(),
            Visible = true,
            Text = "Sentinel Core",
        };

        var menu = new ContextMenuStrip();
        menu.Items.Add("Open Sentinel Core", null, (_, _) => Restore());
        menu.Items.Add(new ToolStripSeparator());
        // Through the window, so it gets its normal teardown - a bare Shutdown() would leave the
        // live tiles' players to be torn down underneath rather than stopped.
        menu.Items.Add("Exit", null, (_, _) => _window.ExitApplication());
        _icon.ContextMenuStrip = menu;

        _icon.DoubleClick += (_, _) => Restore();
        _icon.BalloonTipClicked += (_, _) =>
        {
            Restore();
            if (_lastNotified is { } item) _window.ShowEvent(item);
        };
    }

    public void Notify(IReadOnlyList<ActivityItem> events)
    {
        if (events.Count == 0) return;

        _lastNotified = events[0];

        // One notification however many arrived together. A camera that catches a delivery van
        // pulling up can produce several alerts in one refresh, and three balloons stacking up
        // says nothing the first one didn't.
        var title = events.Count == 1
            ? events[0].Camera
            : $"{events.Count} alerts across {events.Select(item => item.Camera).Distinct().Count()} cameras";

        var body = events.Count == 1
            ? Summarize(events[0])
            : string.Join("\n", events.Take(3).Select(item => $"{item.Camera}: {Summarize(item)}"));

        _icon.ShowBalloonTip(10_000, title, body, ToolTipIcon.Info);
    }

    private static string Summarize(ActivityItem item)
    {
        if (!item.IsAnalyzed) return item.ZoneName is { Length: > 0 } zone ? $"Motion in {zone}" : "Motion";
        return string.IsNullOrWhiteSpace(item.Summary) ? "Something was detected." : item.Summary!;
    }

    private void Restore()
    {
        // Show() as well as un-minimising: closing the window now hides it rather than ending the
        // app, so the window this is asked to bring back may not be on screen at all.
        _window.Show();
        if (_window.WindowState == WindowState.Minimized) _window.WindowState = WindowState.Normal;
        _window.Activate();
    }

    private static Icon LoadIcon()
    {
        try
        {
            var stream = Application.GetResourceStream(
                new Uri("Resources/SentinelCore.ico", UriKind.Relative))?.Stream;
            if (stream is not null) return new Icon(stream);
        }
        catch (Exception)
        {
            // Falls through to the system default below - a missing icon is not worth refusing to
            // show notifications over.
        }
        return SystemIcons.Application;
    }

    public void Dispose()
    {
        _icon.Visible = false;
        _icon.Dispose();
    }
}
