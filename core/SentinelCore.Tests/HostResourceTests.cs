using Xunit;

namespace SentinelCore.Tests;

// Server Health used to report this process's own CPU and working set, which on the deployed box
// meant a permanent "0% CPU, 0.4 GB of 16 GB" no matter how loaded the machine actually was - the
// process sits waiting while the ffmpeg workers and the vision backend do the work. These cover
// the /proc parsing that replaced it; the sample text below is captured from the real box.
public sealed class HostResourceTests
{
    private const string ProcStatLine =
        "cpu  1054127 3908 260616 20946294 15931 0 21766 0 0 0";

    private static readonly string[] MemInfo =
    [
        "MemTotal:       15727856 kB",
        "MemFree:          289176 kB",
        "MemAvailable:    4393076 kB",
        "Buffers:          182704 kB",
        "Cached:          4571336 kB",
        "SwapCached:       208412 kB",
        "SwapTotal:       4194300 kB",
        "SwapFree:          63996 kB",
    ];

    [Fact]
    public void CpuTicksCountIdleAndIowaitAsIdle()
    {
        var ticks = LinuxHostResourceReader.ParseCpuTicks(ProcStatLine);

        Assert.NotNull(ticks);

        // Sum of the eight fields, and busy is everything except idle (20946294) + iowait (15931).
        Assert.Equal(22302642UL, ticks!.Value.Total);
        Assert.Equal(1340417UL, ticks.Value.Busy);
    }

    [Fact]
    public void CpuPercentIsBusyOverTotalWithNoCoreCountDivision()
    {
        // Two readings a tick-second apart on an 8-core box with four cores fully busy: 800 ticks
        // of wall time across the cores, 400 of them busy. The answer is 50%, not 50/8 - the
        // counters are already summed over every core, which is exactly the division the old
        // process-scoped math had to do and this one must not.
        var first = LinuxHostResourceReader.ParseCpuTicks("cpu  1000 0 0 1000 0 0 0 0")!.Value;
        var second = LinuxHostResourceReader.ParseCpuTicks("cpu  1400 0 0 1400 0 0 0 0")!.Value;

        var percent = (second.Busy - first.Busy) / (double)(second.Total - first.Total) * 100.0;

        Assert.Equal(50.0, percent, 3);
    }

    [Fact]
    public void MemoryUsedIsTotalMinusAvailableNotMinusFree()
    {
        var memory = LinuxHostResourceReader.ParseMemory(MemInfo);

        Assert.NotNull(memory);
        Assert.Equal(15727856L * 1024, memory!.Value.TotalBytes);

        // MemTotal - MemAvailable. Using MemFree instead would count reclaimable page cache as
        // used and show any box that has been up for a while at ~98%.
        Assert.Equal((15727856L - 4393076) * 1024, memory.Value.UsedBytes);

        var percent = memory.Value.UsedBytes * 100.0 / memory.Value.TotalBytes;
        Assert.InRange(percent, 70, 75);
    }

    [Fact]
    public void MemoryFallsBackToFreePlusReclaimableWhenMemAvailableIsAbsent()
    {
        // Kernels older than 3.14 don't publish MemAvailable.
        var withoutAvailable = MemInfo.Where(line => !line.StartsWith("MemAvailable:")).ToArray();

        var memory = LinuxHostResourceReader.ParseMemory(withoutAvailable);

        Assert.NotNull(memory);
        Assert.Equal((15727856L - 289176 - 182704 - 4571336) * 1024, memory!.Value.UsedBytes);
    }

    [Fact]
    public void SwapCachedIsNotMistakenForCached()
    {
        // "SwapCached:" must not satisfy the "Cached:" prefix - it would inflate reclaimable
        // memory and understate what is in use.
        var onlySwapCached = new[] { "MemTotal:       1000 kB", "MemFree:  100 kB", "SwapCached: 400 kB" };

        var memory = LinuxHostResourceReader.ParseMemory(onlySwapCached);

        Assert.NotNull(memory);
        Assert.Equal(900L * 1024, memory!.Value.UsedBytes);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("cpu0 1 2 3 4 5 6 7 8")]       // per-core line, not the aggregate
    [InlineData("cpu  not a number")]
    [InlineData("cpu  0 0 0 0 0 0 0 0")]       // a machine with no ticks yet means nothing to report
    public void UnreadableCpuLinesReportNothingRatherThanZeroPercent(string? line)
    {
        Assert.Null(LinuxHostResourceReader.ParseCpuTicks(line));
    }

    [Fact]
    public void MeminfoWithoutMemTotalReportsNothing()
    {
        Assert.Null(LinuxHostResourceReader.ParseMemory(["MemFree:  100 kB"]));
    }
}
