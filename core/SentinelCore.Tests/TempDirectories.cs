using Microsoft.Data.Sqlite;

namespace SentinelCore.Tests;

// Removing a test's temporary data directory, on an operating system that minds about open files.
//
// Nearly every store here is SQLite, and Microsoft.Data.Sqlite pools connections: disposing a
// SqliteConnection hands it back to the pool rather than closing the file. On Linux that is
// invisible, because a file can be unlinked while it is still open. On Windows the delete fails
// outright with "the process cannot access the file 'schedules.db' because it is being used by
// another process", and roughly a third of this suite went red on that alone - every one of them
// having already passed its actual assertions.
//
// So the pools are drained first. The retry after that covers the short window where Windows has
// accepted the close but not yet released the handle, and a final failure is swallowed on purpose:
// this is cleanup, not an assertion, and a passing behaviour test should not go red because a
// directory under %TEMP% outlived it by a few seconds.
internal static class TempDirectories
{
    private const int Attempts = 5;

    public static void Delete(string path)
    {
        if (string.IsNullOrEmpty(path) || !Directory.Exists(path)) return;

        // Every pooled connection this test opened, closed for real.
        SqliteConnection.ClearAllPools();

        for (var attempt = 1; attempt <= Attempts; attempt++)
        {
            try
            {
                Directory.Delete(path, recursive: true);
                return;
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
                if (attempt == Attempts) return;
                Thread.Sleep(50 * attempt);
            }
        }
    }
}
