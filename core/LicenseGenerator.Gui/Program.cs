using Avalonia;

namespace LicenseGenerator.Gui;

internal static class Program
{
    // Avalonia requires STA and no synchronization context before AppMain runs, hence the
    // separate entry point rather than top-level statements like the CLI next door.
    //
    // Deliberately no Dispatcher.UIThread.UnhandledException backstop: subscribing to it throws
    // PlatformNotSupportedException out of Dispatcher.MainLoop on macOS, because the native
    // NSApplication run loop drives the dispatcher rather than the other way round. Every click
    // handler therefore has to catch its own failures, which they do.
    [STAThread]
    public static void Main(string[] args) =>
        BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);

    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .WithInterFont()
            .LogToTrace();
}
