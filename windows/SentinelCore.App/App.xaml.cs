using System.Windows;
using System.Windows.Threading;
using SentinelCore.App.Design;
using SentinelCore.Client;

namespace SentinelCore.App;

public partial class App : Application
{
    public static AppModel Model { get; private set; } = null!;

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

        MainWindow = new MainWindow(Model);
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
        Model.Dispose();
        base.OnExit(e);
    }
}
