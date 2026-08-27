using Microsoft.Data.Sqlite;
using SentinelCore.Web;
using Xunit;

namespace SentinelCore.Tests;

// The two pieces of the recording feature that are pure and therefore worth pinning: deciding
// which window (if any) is due right now, and turning two clock times into a duration.
//
// Everything the timing itself does - day-of-week sets, cron, DST - is ScheduleTiming's, and is
// already covered by ScheduleTimingTests. What is tested here is only what this feature adds on
// top: that a window already ended is skipped rather than caught up, that one underway is joined
// late, and that a stop time before its start means "crosses midnight" rather than a negative
// length.
public sealed class ScheduledRecordingTests
{
    private static readonly TimeZoneInfo NewYork = TimeZoneInfo.FindSystemTimeZoneById("America/New_York");

    private static RecordingSchedule Daily(string startTime, int durationSeconds, DateTimeOffset? lastStarted = null) =>
        new(
            Guid.NewGuid(),
            Guid.NewGuid(),
            "Evening driveway",
            Enabled: true,
            RecordingSchedule.KindDaily,
            startTime,
            DaysOfWeek: [],
            CronExpression: string.Empty,
            RunAtUtc: null,
            SolarEvent: string.Empty,
            SolarOffsetMinutes: 0,
            SolarElevationDegrees: 0,
            durationSeconds,
            IncludeAudio: false,
            lastStarted,
            LastStatus: string.Empty,
            LastDetail: string.Empty,
            DateTimeOffset.UnixEpoch,
            DateTimeOffset.UnixEpoch);

    // 20:00 America/New_York on a summer evening (EDT, UTC-4) is 00:00 UTC the next day.
    private static DateTimeOffset Utc(int year, int month, int day, int hour, int minute) =>
        new(year, month, day, hour, minute, 0, TimeSpan.Zero);

    [Fact]
    public void AWindowThatHasNotStartedYetIsNotDue()
    {
        var schedule = Daily("20:00", 1800);

        // 19:30 local - half an hour early.
        var due = RecordingScheduleWorker.FindDueWindow(schedule, Utc(2026, 7, 1, 23, 30), NewYork, null);

        Assert.Null(due);
    }

    [Fact]
    public void AWindowUnderwayIsDueSoARestartRejoinsIt()
    {
        var schedule = Daily("20:00", 1800);

        // 20:05 local: five minutes in. The remaining twenty-five are still worth having, which is
        // the whole point of resuming rather than writing the window off.
        var due = RecordingScheduleWorker.FindDueWindow(schedule, Utc(2026, 7, 2, 0, 5), NewYork, null);

        Assert.NotNull(due);
        Assert.Equal(Utc(2026, 7, 2, 0, 0), due!.Value.Start);
        Assert.Equal(Utc(2026, 7, 2, 0, 30), due.Value.End);
    }

    [Fact]
    public void AWindowThatAlreadyEndedIsSkippedRatherThanCaughtUp()
    {
        var schedule = Daily("20:00", 1800);

        // 21:00 local, an hour after the window opened and half an hour after it shut. Recording
        // now would capture the wrong half hour - the footage of 20:00 is simply gone.
        var due = RecordingScheduleWorker.FindDueWindow(schedule, Utc(2026, 7, 2, 1, 0), NewYork, null);

        Assert.Null(due);
    }

    [Fact]
    public void AScheduleThatAlreadyRanTodayDoesNotFireAgain()
    {
        // LastStartedUtc is set to today's window, which is what the worker writes the moment it
        // starts one. Without this the next pass, thirty seconds later, would start a second
        // recording of the same window on top of the first.
        var start = Utc(2026, 7, 2, 0, 0);
        var schedule = Daily("20:00", 1800, lastStarted: start);

        var due = RecordingScheduleWorker.FindDueWindow(schedule, start.AddMinutes(5), NewYork, null);

        Assert.Null(due);
    }

    [Fact]
    public void ADormantScheduleDoesNotOweAWeekOfMissedWindows()
    {
        // Last ran a fortnight ago - the machine was off. Every window in between has ended, so
        // none of them is due, and the walk must terminate rather than run to its limit.
        var schedule = Daily("20:00", 1800, lastStarted: Utc(2026, 6, 18, 0, 0));

        var due = RecordingScheduleWorker.FindDueWindow(schedule, Utc(2026, 7, 2, 1, 0), NewYork, null);

        Assert.Null(due);
    }

    [Fact]
    public void StopTimeBeforeStartTimeIsAWindowCrossingMidnight()
    {
        var dto = Dto(startTime: "23:00", stopTime: "01:00");

        var errors = RecordingEndpoints.Validate(dto, out var duration);

        Assert.Empty(errors);
        Assert.Equal((int)TimeSpan.FromHours(2).TotalSeconds, duration);
    }

    [Fact]
    public void StopTimeAfterStartTimeIsTheObviousDifference()
    {
        var errors = RecordingEndpoints.Validate(Dto("20:00", "20:30"), out var duration);

        Assert.Empty(errors);
        Assert.Equal(1800, duration);
    }

    [Fact]
    public void AWindowShorterThanTheMinimumIsRefused()
    {
        var errors = RecordingEndpoints.Validate(Dto("20:00", "20:00", durationSeconds: 30), out _);

        Assert.Contains(errors, error => error.Contains("at least", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void AWindowLongerThanADayIsRefused()
    {
        var errors = RecordingEndpoints.Validate(
            Dto("20:00", "20:00", durationSeconds: RecordingSchedule.MaximumDurationSeconds + 1), out _);

        Assert.Contains(errors, error => error.Contains("longer than", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void AWindowWithNeitherStopTimeNorDurationIsRefused()
    {
        var errors = RecordingEndpoints.Validate(Dto("20:00", stopTime: string.Empty), out _);

        Assert.Contains(errors, error => error.Contains("how long", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void AnUnparseableCronExpressionIsRefused()
    {
        var dto = Dto("", "", durationSeconds: 1800) with { Kind = RecordingSchedule.KindCron, CronExpression = "not a cron" };

        var errors = RecordingEndpoints.Validate(dto, out _);

        Assert.Contains(errors, error => error.Contains("cron", StringComparison.OrdinalIgnoreCase));
    }

    private static RecordingScheduleDto Dto(string startTime, string stopTime, int? durationSeconds = null) =>
        new(
            Id: null,
            "Evening driveway",
            Enabled: true,
            RecordingSchedule.KindDaily,
            startTime,
            stopTime,
            DaysOfWeek: [],
            CronExpression: string.Empty,
            RunAtUtc: null,
            SolarEvent: string.Empty,
            SolarOffsetMinutes: 0,
            SolarElevationDegrees: 0,
            durationSeconds,
            IncludeAudio: false);
}

// The storage half of a recording started by hand. The recording itself needs ffmpeg and a camera,
// so what is pinned here is what the rest of the feature relies on being true: that a hand-started
// row can be told apart from a scheduled one after a restart, and that stopping one by hand can
// move the window it is measured against.
public sealed class ManualRecordingStoreTests : IDisposable
{
    private readonly string _directory = Path.Combine(
        Path.GetTempPath(), $"sentinel-manual-{Guid.NewGuid():N}");

    private SqliteRecordingStore NewStore() => new(_directory);

    private static Recording Row(Guid cameraId, bool isManual, DateTimeOffset start, DateTimeOffset end) =>
        new(
            Guid.NewGuid(), cameraId, ScheduleId: null, "Front door (recorded by hand)",
            start, end, ActualStartUtc: null, ActualEndUtc: null, FilePath: null, ByteSize: 0,
            Recording.StatusRecording, string.Empty, IsManual: isManual);

    [Fact]
    public async Task A_recording_started_by_hand_still_says_so_after_a_restart()
    {
        var cameraId = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;

        // Two rows that are identical except for the flag - both have no schedule, which is exactly
        // the ambiguity the flag exists to resolve. The second stands for a scheduled recording
        // whose schedule was deleted while its footage outlived it.
        using (var store = NewStore())
        {
            IRecordingStore recordings = store;
            await recordings.AddAsync(Row(cameraId, isManual: true, now, now.AddMinutes(10)), TestContext.Current.CancellationToken);
            await recordings.AddAsync(Row(cameraId, isManual: false, now, now.AddMinutes(30)), TestContext.Current.CancellationToken);
        }

        // A new store over the same file is what the next process start actually sees.
        using (var store = NewStore())
        {
            var inProgress = await ((IRecordingStore)store).GetInProgressAsync(TestContext.Current.CancellationToken);

            Assert.Equal(2, inProgress.Count);
            Assert.Single(inProgress, recording => recording.IsManual);
            Assert.Single(inProgress, recording => !recording.IsManual);
        }
    }

    [Fact]
    public async Task Stopping_by_hand_moves_the_window_the_footage_is_measured_against()
    {
        var now = DateTimeOffset.UtcNow;
        using var store = NewStore();
        IRecordingStore recordings = store;

        var recording = Row(Guid.NewGuid(), isManual: true, now, now.AddMinutes(10));
        await recordings.AddAsync(recording, TestContext.Current.CancellationToken);

        // Stopped after half a minute of a ten-minute cap. Without this the recorder would compare
        // 30 seconds of footage against a ten-minute window and file a perfectly good recording as
        // partial - see ScheduledRecorder.RunAsync.
        var stoppedAt = now.AddSeconds(30);
        await recordings.SetRequestedEndAsync(recording.Id, stoppedAt, TestContext.Current.CancellationToken);

        var stored = await recordings.GetAsync(recording.Id, TestContext.Current.CancellationToken);

        Assert.NotNull(stored);
        Assert.Equal(stoppedAt, stored!.RequestedEndUtc, TimeSpan.FromMilliseconds(1));
        Assert.Equal(now, stored.RequestedStartUtc, TimeSpan.FromMilliseconds(1));
    }

    [Fact]
    public async Task A_database_written_before_the_record_button_existed_still_opens()
    {
        Directory.CreateDirectory(_directory);
        var databasePath = Path.Combine(_directory, "recordings.db");
        var rowId = Guid.NewGuid();

        // The Recordings table exactly as it was before IsManual was added, with a row in it. Every
        // installation upgrading to this feature has one of these.
        await using (var connection = new SqliteConnection($"Data Source={databasePath}"))
        {
            await connection.OpenAsync(TestContext.Current.CancellationToken);
            await using var command = connection.CreateCommand();
            command.CommandText = """
                CREATE TABLE Recordings (
                    Id TEXT PRIMARY KEY,
                    CameraId TEXT NOT NULL,
                    ScheduleId TEXT,
                    Name TEXT NOT NULL,
                    RequestedStartUtc TEXT NOT NULL,
                    RequestedEndUtc TEXT NOT NULL,
                    ActualStartUtc TEXT,
                    ActualEndUtc TEXT,
                    FilePath TEXT,
                    ByteSize INTEGER NOT NULL,
                    Status TEXT NOT NULL,
                    Detail TEXT NOT NULL
                );
                INSERT INTO Recordings
                    (Id, CameraId, ScheduleId, Name, RequestedStartUtc, RequestedEndUtc,
                     ByteSize, Status, Detail)
                VALUES
                    ($id, $cameraId, NULL, 'Evening driveway', '2026-08-01T20:00:00.0000000+00:00',
                     '2026-08-01T20:30:00.0000000+00:00', 0, 'complete', '');
                """;
            command.Parameters.AddWithValue("$id", rowId.ToString("N"));
            command.Parameters.AddWithValue("$cameraId", Guid.NewGuid().ToString("N"));
            await command.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
        }

        using var store = NewStore();
        var migrated = await ((IRecordingStore)store).GetAsync(rowId, TestContext.Current.CancellationToken);

        // Not manual, because manual recordings did not exist when it was written.
        Assert.NotNull(migrated);
        Assert.False(migrated!.IsManual);
    }

    public void Dispose()
    {
        // The store holds the connection open until it is disposed, which the tests do themselves.
        SqliteConnection.ClearAllPools();
        try { if (Directory.Exists(_directory)) Directory.Delete(_directory, recursive: true); }
        catch (IOException) { /* a locked file in a temp directory is not a failed test */ }
    }
}
