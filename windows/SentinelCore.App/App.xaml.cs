using System.Windows;
using System.Windows.Threading;
using SentinelCore.App.Design;
using SentinelCore.Client;
using SentinelCore.Client.Organizer;

namespace SentinelCore.App;

public partial class App : Application
{
    public static AppModel Model { get; private set; } = null!;

    /// <summary>The Organizer's copy on this PC and its sync. Null only if its database couldn't
    /// be opened, in which case the section simply isn't there.</summary>
    public static OrganizerSession? Organizer { get; private set; }

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // The dispatcher context, handed over explicitly. Everything the model loads arrives on a
        // thread-pool thread, and every notification it raises has to be marshalled back here
        // before a window handler touches a control.
        Model = new AppModel(uiContext: new DispatcherSynchronizationContext(Dispatcher));
        ImageLoader.Attach(Model.Client);

        // A monitoring client that vanishes without saying why is worse than useless - the whole
        // point of it is to be trusted to still be running. Anything that escapes a handler is
        // shown, written to the in-app log, and then the app carries on if it possibly can.
        DispatcherUnhandledException += OnDispatcherUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
        {
            if (args.ExceptionObject is Exception error) Model.Log($"Unhandled: {error}", DiagnosticLevel.Error);
        };
        TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            Model.Log($"Unobserved background failure: {args.Exception.Message}", DiagnosticLevel.Error);
            args.SetObserved();
        };

        try
        {
            Organizer = new OrganizerSession(Model);
        }
        catch (Exception error)
        {
            Model.Log($"The Organizer's copy on this PC couldn't be opened: {error.Message}", DiagnosticLevel.Error);
        }

        MainWindow = new MainWindow(Model, Organizer);
        MainWindow.Show();
    }

    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs args)
    {
        Model.Log($"Unhandled: {args.Exception}", DiagnosticLevel.Error);
        MessageBox.Show(
            $"Something went wrong:\n\n{args.Exception.Message}\n\nThe app will keep running. " +
            "The Connection Log under Settings has the details.",
            "Sentinel Core",
            MessageBoxButton.OK,
            MessageBoxImage.Warning);
        args.Handled = true;
    }

    protected override void OnExit(ExitEventArgs e)
    {
        Organizer?.Dispose();
        Model.Dispose();
        base.OnExit(e);
    }
}
