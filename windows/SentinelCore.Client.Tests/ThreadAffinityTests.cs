using SentinelCore.Client;
using Xunit;

namespace SentinelCore.Client.Tests;

/// <summary>
/// Everything this model tells the UI has to arrive on the UI's own thread.
/// </summary>
/// <remarks>
/// This is not a theoretical concern. Nearly every property here is written from the tail of an
/// awaited HTTP call, which resumes on a thread-pool thread. When those notifications were raised
/// there, the window's handler threw a cross-thread exception the moment it touched a control;
/// that exception escaped through the property setter into the middle of ConnectAsync, and the app
/// sat showing an empty main screen with the badge stuck on "Connecting…" - against a server that
/// was answering perfectly and simply wanted a login. Nothing in the app said so, because the code
/// that would have said so was the code that threw.
/// </remarks>
public class ThreadAffinityTests : IDisposable
{
    private readonly string _directory =
        Path.Combine(Path.GetTempPath(), "sentinel-tests", Guid.NewGuid().ToString("N"));

    /// <summary>
    /// Stands in for WPF's dispatcher: it records the thread it was created on and runs everything
    /// posted to it there, exactly as the real one does.
    /// </summary>
    private sealed class RecordingContext : SynchronizationContext
    {
        private readonly List<Action> _pending = new();

        public int OwnerThreadId { get; } = Environment.CurrentManagedThreadId;

        public int PostCount { get; private set; }

        public override void Post(SendOrPostCallback callback, object? state)
        {
            lock (_pending)
            {
                PostCount++;
                _pending.Add(() => callback(state));
            }
        }

        public override void Send(SendOrPostCallback callback, object? state) => callback(state);

        /// <summary>Runs what has been queued, the way a message loop would.</summary>
        public void Drain()
        {
            List<Action> work;
            lock (_pending)
            {
                work = new List<Action>(_pending);
                _pending.Clear();
            }
            foreach (var action in work) action();
        }
    }

    private AppModel NewModel(SynchronizationContext? uiContext = null)
    {
        Directory.CreateDirectory(_directory);
        return new AppModel(
            new PreferenceStore(Path.Combine(_directory, "settings.json")),
            new Networking.CredentialStore(Path.Combine(_directory, "credentials")),
            uiContext);
    }

    [Fact]
    public void A_property_changed_on_a_background_thread_notifies_on_the_UI_thread()
    {
        var context = new RecordingContext();
        using var model = NewModel(context);

        var notifiedOn = new List<int>();
        model.PropertyChanged += (_, _) => notifiedOn.Add(Environment.CurrentManagedThreadId);

        // Exactly what the tail of an awaited HTTP call does.
        // Deliberately not awaited: this test IS the UI thread, and awaiting here would queue the
        // continuation onto the very context the test has not drained yet.
        Task.Run(() => model.ServerUrl = "http://192.168.1.50:3320").GetAwaiter().GetResult();

        // Nothing has reached the handler yet - it is queued for the UI thread, not dropped.
        Assert.Empty(notifiedOn);
        Assert.True(context.PostCount > 0);

        context.Drain();

        Assert.NotEmpty(notifiedOn);
        Assert.All(notifiedOn, threadId => Assert.Equal(context.OwnerThreadId, threadId));
    }

    [Fact]
    public void A_collection_changed_on_a_background_thread_also_lands_on_the_UI_thread()
    {
        var context = new RecordingContext();
        using var model = NewModel(context);

        var notifiedOn = new List<int>();
        model.Diagnostics.CollectionChanged += (_, _) => notifiedOn.Add(Environment.CurrentManagedThreadId);

        Task.Run(() => model.Log("something happened on a worker thread")).GetAwaiter().GetResult();

        Assert.Empty(notifiedOn);
        context.Drain();

        Assert.NotEmpty(notifiedOn);
        Assert.All(notifiedOn, threadId => Assert.Equal(context.OwnerThreadId, threadId));
    }

    [Fact]
    public void A_handler_that_throws_does_not_escape_into_the_connect_that_triggered_it()
    {
        // The failure mode this whole file exists for: a subscriber blowing up must not tear down
        // the connection sequence half-way through and leave the app in a state nothing explains.
        var context = new RecordingContext();
        using var model = NewModel(context);

        model.PropertyChanged += (_, _) => throw new InvalidOperationException("a window handler failed");

        var setter = Task.Run(() => model.ServerUrl = "http://192.168.1.50:3320");
        setter.GetAwaiter().GetResult();

        Assert.True(setter.IsCompletedSuccessfully);
        Assert.Equal("http://192.168.1.50:3320", model.ServerUrl);
    }

    [Fact]
    public void With_no_synchronization_context_notifications_run_inline()
    {
        // Console tools have no dispatcher; the model still has to work there.
        SynchronizationContext.SetSynchronizationContext(null);
        using var model = NewModel();

        var notified = false;
        model.PropertyChanged += (_, _) => notified = true;
        model.ServerUrl = "http://192.168.1.50:3320";

        Assert.True(notified);
    }

    public void Dispose()
    {
        SynchronizationContext.SetSynchronizationContext(null);
        try
        {
            if (Directory.Exists(_directory)) Directory.Delete(_directory, recursive: true);
        }
        catch (IOException)
        {
        }
    }
}
