using System.Collections.Concurrent;
using SentinelCore;

namespace SentinelCore.Web;

// What a client needs to draw the record button: whether this camera is recording, and when it
// will stop itself if nobody presses the button again.
public sealed record ManualRecordingState(
    bool IsRecording,
    Guid? RecordingId,
    DateTimeOffset? StartedUtc,
    DateTimeOffset? StopsAtUtc,
    int MaxMinutes)
{
    public static ManualRecordingState Idle(int maxMinutes) => new(false, null, null, null, maxMinutes);
}

// Holds the camera recordings somebody started by hand and can stop the same way.
//
// The recording itself is ScheduledRecorder's, unchanged: this only decides when one starts, hands
// it a stop signal, and remembers which camera it belongs to. That reuse is the whole design -
// hand-started footage lands in the same Recordings list, ages out under the same retention, and
// stops itself at the same free-space floor as a scheduled window, because it *is* one, minus the
// clock that would have opened it.
//
// State lives here rather than on the client so a recording started on a phone can be stopped from
// the Windows app, or from the same phone after the app was killed and reopened. A client asking
// what the button should say gets the server's answer, not its own memory of a press.
public sealed class ManualRecordingService(
    IRecordingStore recordings,
    ScheduledRecorder recorder,
    IHostApplicationLifetime lifetime,
    ILogger<ManualRecordingService> logger)
{
    // One per camera. A second press on a camera already recording is a stop, never a second
    // recording - two ffmpegs writing the same camera is twice the disk for the same footage.
    private readonly ConcurrentDictionary<Guid, Session> _active = new();

    // How long a stop waits for the file to be concatenated before answering anyway. The copy runs
    // at disk speed - a ten-minute recording is a couple of seconds - so this is only reached when
    // something is wrong, and the caller is told "still finalizing" rather than left hanging.
    private static readonly TimeSpan FinalizeWait = TimeSpan.FromSeconds(20);

    private sealed record Session(Recording Recording, CancellationTokenSource Stop, Task Completion);

    public async Task<int> MaxMinutesAsync(CancellationToken cancellationToken) =>
        Math.Max(1, (await recorder.GetOptionsAsync(cancellationToken)).ManualMaxMinutes);

    public async Task<ManualRecordingState> StateAsync(Guid cameraId, CancellationToken cancellationToken)
    {
        var maxMinutes = await MaxMinutesAsync(cancellationToken);
        return _active.TryGetValue(cameraId, out var session)
            ? Describe(session, maxMinutes)
            : ManualRecordingState.Idle(maxMinutes);
    }

    private static ManualRecordingState Describe(Session session, int maxMinutes) => new(
        true,
        session.Recording.Id,
        session.Recording.RequestedStartUtc,
        session.Recording.RequestedEndUtc,
        maxMinutes);

    // Starts recording, or reports that this camera already is. Never throws for an ordinary
    // failure - the camera not answering is something ScheduledRecorder writes onto the row.
    public async Task<ManualRecordingState> StartAsync(Camera camera, CancellationToken cancellationToken)
    {
        var maxMinutes = await MaxMinutesAsync(cancellationToken);

        // Cheap pre-check so the common "already recording" answer costs no database write. The
        // real guard is the TryAdd below, which is what makes two presses arriving together safe.
        if (_active.TryGetValue(camera.Id, out var running)) return Describe(running, maxMinutes);

        var now = DateTimeOffset.UtcNow;
        var recording = new Recording(
            Guid.NewGuid(),
            camera.Id,
            ScheduleId: null,
            $"{camera.Name} (recorded by hand)",
            now,
            now.AddMinutes(maxMinutes),
            ActualStartUtc: null,
            ActualEndUtc: null,
            FilePath: null,
            ByteSize: 0,
            Recording.StatusRecording,
            string.Empty,
            IsManual: true);

        var stop = new CancellationTokenSource();

        // The session has to be complete before it is published, because the recording it describes
        // can finish at any moment after it starts - a camera that refuses the connection finishes
        // almost immediately. Publishing a placeholder and patching it afterwards loses that race:
        // the cleanup runs, removes the entry, and then the patch puts a dead one back, leaving a
        // camera that says it is recording forever. Hence a signal completed by the run itself
        // rather than the run's own Task.
        var finished = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var session = new Session(recording, stop, finished.Task);

        // Reserved before the row is written and before ffmpeg starts, so a double-tap cannot get
        // two recordings underway.
        if (!_active.TryAdd(camera.Id, session))
        {
            stop.Dispose();
            return await StateAsync(camera.Id, cancellationToken);
        }

        try
        {
            await recordings.AddAsync(recording, cancellationToken);
        }
        catch
        {
            _active.TryRemove(camera.Id, out _);
            stop.Dispose();
            throw;
        }

        logger.LogInformation(
            "Recording {CameraName} by hand - up to {Minutes} minute(s), until {StopsAt:HH:mm:ss} local",
            camera.Name, maxMinutes, recording.RequestedEndUtc.ToLocalTime());

        // Not the request's token: the recording outlives the HTTP call that started it by
        // definition. The application-stopping token is the one that should end it, and it makes
        // RunAsync finalize what it captured on the way down.
        _ = Task.Run(async () =>
        {
            try
            {
                await recorder.RunAsync(camera, recording, lifetime.ApplicationStopping, stop.Token);
            }
            catch (OperationCanceledException)
            {
                // Shutdown. RunAsync has already finalized whatever was captured.
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Hand-started recording {RecordingId} ended badly", recording.Id);
            }
            finally
            {
                // Signalled last, so anyone woken by it finds the camera already idle rather than
                // reading a session that is about to disappear.
                _active.TryRemove(camera.Id, out _);
                stop.Dispose();
                finished.TrySetResult();
            }
        }, CancellationToken.None);

        return Describe(session, maxMinutes);
    }

    // Stops a recording and waits, briefly, for the file to exist. Returns the finished row when it
    // got one, so the caller can say how long was kept rather than just "stopped".
    public async Task<Recording?> StopAsync(Guid cameraId, CancellationToken cancellationToken)
    {
        if (!_active.TryGetValue(cameraId, out var session)) return null;

        try { await session.Stop.CancelAsync(); }
        catch (ObjectDisposedException) { /* it finished on its own between the lookup and here */ }

        try
        {
            await session.Completion.WaitAsync(FinalizeWait, cancellationToken);
        }
        catch (TimeoutException)
        {
            logger.LogWarning(
                "Recording {RecordingId} was still finalizing {Seconds}s after it was stopped",
                session.Recording.Id, FinalizeWait.TotalSeconds);
        }

        // Re-read rather than trusting the in-memory row: the status, the file and its size are all
        // written by the recorder after this object was made.
        return await recordings.GetAsync(session.Recording.Id, cancellationToken);
    }

    public bool IsRecording(Guid cameraId) => _active.ContainsKey(cameraId);

    // Every camera recording by hand right now. The camera list uses this to light up more than one
    // tile without asking per camera.
    public IReadOnlyCollection<Guid> ActiveCameraIds() => _active.Keys.ToList();
}
