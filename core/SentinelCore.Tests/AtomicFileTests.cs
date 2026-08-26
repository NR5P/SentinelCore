using SentinelCore;
using Xunit;

namespace SentinelCore.Tests;

// The behaviour these cover only differs by platform in one direction: on Linux and macOS the
// first move always wins, so the locked-destination test passes there without ever reaching the
// retry. It is Windows that needs the retry, and Windows that this catches when it goes missing.
public class AtomicFileTests
{
    [Fact]
    public void Replace_OverAFreeDestination_LeavesTheNewContent()
    {
        var directory = NewDirectory();
        try
        {
            var final = Path.Combine(directory, "settings.json");
            var temporary = final + ".tmp";
            File.WriteAllText(final, "old");
            File.WriteAllText(temporary, "new");

            AtomicFile.Replace(temporary, final);

            Assert.Equal("new", File.ReadAllText(final));
            Assert.False(File.Exists(temporary));
        }
        finally
        {
            TempDirectories.Delete(directory);
        }
    }

    [Fact]
    public void Replace_WhileTheDestinationIsHeldOpen_StillLandsOnceItIsReleased()
    {
        var directory = NewDirectory();
        try
        {
            var final = Path.Combine(directory, "settings.json");
            var temporary = final + ".tmp";
            File.WriteAllText(final, "old");
            File.WriteAllText(temporary, "new");

            // Held on this thread and released from another, because the whole point is that the
            // handle goes away *while* Replace is still trying. A pool thread can be queued behind
            // the rest of this suite for longer than any budget worth giving a save path, so this
            // uses a dedicated one.
            var handle = new FileStream(final, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
            var release = new Thread(() =>
            {
                Thread.Sleep(60);
                handle.Dispose();
            }) { IsBackground = true };
            release.Start();

            AtomicFile.Replace(temporary, final);
            release.Join();

            Assert.Equal("new", File.ReadAllText(final));
        }
        finally
        {
            TempDirectories.Delete(directory);
        }
    }

    [Fact]
    public void Replace_WhenTheDestinationNeverFreesUp_ReportsTheFailure()
    {
        var directory = NewDirectory();
        try
        {
            var final = Path.Combine(directory, "settings.json");
            var temporary = final + ".tmp";
            File.WriteAllText(final, "old");
            File.WriteAllText(temporary, "new");

            using var handle = new FileStream(final, FileMode.Open, FileAccess.ReadWrite, FileShare.None);

            if (OperatingSystem.IsWindows())
            {
                // Retrying decides how long to keep trying, never whether to report a failure - a
                // caller that swallows this is making its own choice, and one that doesn't must
                // still hear about it.
                //
                // Which of the two it is depends on how the destination is held: Windows reports
                // this particular case as UnauthorizedAccessException, which does not derive from
                // IOException. Catching only one of them would have left the other unretried.
                var error = Record.Exception(() => AtomicFile.Replace(temporary, final));
                Assert.NotNull(error);
                Assert.True(
                    error is IOException or UnauthorizedAccessException,
                    $"Replace reported a {error!.GetType().Name}, which AtomicFile does not retry.");
            }
            else
            {
                // rename(2) doesn't care about the open handle, so there is no failure to report.
                AtomicFile.Replace(temporary, final);
                Assert.Equal("new", File.ReadAllText(final));
            }
        }
        finally
        {
            TempDirectories.Delete(directory);
        }
    }

    private static string NewDirectory()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"atomic-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        return directory;
    }
}
