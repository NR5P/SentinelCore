using System.Diagnostics;
using Xunit;

namespace SentinelCore.Tests;

// Some tests here stand in for an external binary - ffmpeg, go2rtc - by writing a small
// "#!/bin/sh" script and letting the production code spawn it. That is a good way to test the
// spawning, the argument building and the process lifetime without a real encoder installed, and
// it is entirely a POSIX trick: Windows has no /bin/chmod to mark the file executable and would
// not run a shell script if it did.
//
// So those tests skip on Windows rather than fail. The code they cover is not Windows-specific -
// it starts a process and reads its output - but the harness is, and a red suite that only means
// "this box is not Linux" tells nobody anything. Everything else in this project does run here.
internal static class PosixShellStubs
{
    public static bool Available => OperatingSystem.IsLinux() || OperatingSystem.IsMacOS();

    /// <summary>
    /// Ends the calling test as skipped when shell stubs cannot work here. Safe only from a test
    /// body - xUnit turns a skip raised from a constructor into an error.
    /// </summary>
    public static void SkipIfUnavailable() =>
        Assert.SkipUnless(
            Available,
            "Stands in for an external binary with a /bin/sh script, which needs a POSIX shell and /bin/chmod.");

    /// <summary>
    /// Marks a stub script executable. A no-op off POSIX so a fixture constructor can call it
    /// without blowing up before its tests get the chance to skip themselves.
    /// </summary>
    public static void MakeExecutable(string path)
    {
        if (!Available) return;
        using var chmod = Process.Start("/bin/chmod", $"+x {path}")!;
        chmod.WaitForExit();
    }

    /// <summary>Kills whatever stub processes are still running, by script path.</summary>
    public static void Kill(string path)
    {
        if (!Available) return;
        using var pkill = Process.Start("/usr/bin/pkill", $"-f {path}");
        pkill?.WaitForExit();
    }
}
