using System.Collections.Concurrent;
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
// Draining the pool frees those files, but SqliteConnection.ClearAllPools is process-wide and
// xUnit runs test classes in parallel, so calling it from a test's own cleanup reaches into
// connections other tests are in the middle of using. This code used to do exactly that, and
// about one run in eight died with "ObjectDisposedException: SQLitePCL.sqlite3" inside a store's
// constructor, in a test with no connection to the one doing the cleaning up. The comment here
// claimed ClearAllPools only touches idle connections. It does not.
//
// So the drain happens once, from an assembly fixture, after the last test has finished and there
// is nobody left to surprise. Linux reaches none of this: the first delete succeeds, which is also
// the only path that runs when deploy.sh puts this suite in front of a service restart.
//
// It is not complete even so. Of ~140 directories deferred on a Windows run, the drain recovers
// about 50; the rest are held by test hosts that are never disposed, so their connections are open
// rather than pooled and no amount of draining or retrying frees them - measured, not assumed.
// Those directories are left behind. That is untidiness in %TEMP%, not a failing test, and fixing
// it means fixing host disposal in the tests that leak, which is a separate job.
internal static class TempDirectories
{
    private static readonly ConcurrentBag<string> Deferred = [];

    public static void Delete(string path)
    {
        if (string.IsNullOrEmpty(path) || !Directory.Exists(path)) return;

        try
        {
            Directory.Delete(path, recursive: true);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            // Windows only, in practice. Cleanup is not an assertion: a passing behaviour test
            // must not go red because a directory under the temp folder outlived it.
            Deferred.Add(path);
        }
    }

    internal static void DrainAndDelete()
    {
        // Safe here in the way it was never safe from a running test: by the time the assembly
        // fixture is torn down, there is no other test to take a connection out from under.
        SqliteConnection.ClearAllPools();

        foreach (var path in Deferred)
        {
            try
            {
                if (Directory.Exists(path)) Directory.Delete(path, recursive: true);
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
                // Nothing left to try, and nothing worth failing a finished run over.
            }
        }
    }
}
