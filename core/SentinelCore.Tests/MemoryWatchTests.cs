extern alias WebHost;

using Microsoft.Extensions.Logging.Abstractions;
using SentinelCore.Web;
using Xunit;

namespace SentinelCore.Tests;

// SELF-MONITORING's memory warnings: a program growing past its share is named early, the whole
// box filling up is told late, a brief spike says nothing, and a leak that is killed and regrows
// every few hours (Ollama on the media box) pages at most twice a day.
public sealed class MemoryWatchTests
{
    private const long Gb = 1024L * 1024 * 1024;
    private readonly ManualTime _clock = new(new DateTimeOffset(2026, 10, 3, 20, 0, 0, TimeSpan.Zero));
    private readonly FakeNotifier _notifier = new();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private MemoryWatch Watch() => new(_notifier, NullLogger<MemoryWatch>.Instance, _clock);

    private static MemoryReading Reading(long usedGb, params (string Name, double Gb)[] programs) =>
        new(usedGb * Gb, 14 * Gb, programs.Select(p => new ProgramMemory(p.Name, (long)(p.Gb * Gb), 1)).ToList());

    // Three readings a minute apart, the way the worker calls it.
    private async Task CheckTimes(MemoryWatch watch, MemoryReading reading, int times = MemoryWatch.ConsecutiveChecks)
    {
        for (var i = 0; i < times; i++)
        {
            await watch.CheckAsync(reading, 90, 50, Ct);
            _clock.Now += TimeSpan.FromMinutes(1);
        }
    }

    [Fact]
    public async Task AProgramPastHalf_IsNamed_OnlyAfterThreeReadingsInARow()
    {
        var watch = Watch();
        var leaking = Reading(10, ("llama-server", 7.5), ("SentinelCore", 0.8));

        await CheckTimes(watch, leaking, 2);
        await CheckTimes(watch, Reading(6, ("llama-server", 4)), 1);
        await CheckTimes(watch, leaking, 2);
        Assert.Empty(_notifier.Sent);

        await CheckTimes(watch, leaking, 1);
        var (subject, text) = Assert.Single(_notifier.Sent);
        Assert.Equal("llama-server is using 7.5 GB of memory", subject);
        Assert.Contains("54% of the server's 14.0 GB", text);
        Assert.Contains("sudo systemctl restart ollama", text);
        Assert.Contains("SentinelCore: 0.8 GB", text);
    }

    [Fact]
    public async Task ALeakThatIsKilledAndRegrows_PagesAtMostEveryTwelveHours()
    {
        var watch = Watch();
        var leaking = Reading(12, ("llama-server", 8));

        await CheckTimes(watch, leaking);
        Assert.Single(_notifier.Sent);

        // Killed (gone from the list), back three hours later, over the line again: same trouble.
        await CheckTimes(watch, Reading(4), 1);
        _clock.Now += TimeSpan.FromHours(3);
        await CheckTimes(watch, leaking, 10);
        Assert.Single(_notifier.Sent);

        // Still at it twelve hours after the first warning: say so again.
        _clock.Now += TimeSpan.FromHours(9);
        await CheckTimes(watch, leaking, 1);
        Assert.Equal(2, _notifier.Sent.Count);
    }

    [Fact]
    public async Task TheWholeBoxFilling_IsToldSeparately_WithTheBiggestUsers()
    {
        var watch = Watch();

        await CheckTimes(watch, Reading(13, ("ffmpeg", 3), ("SentinelCore.Web", 2), ("qbittorrent-nox", 1)));

        var (subject, text) = Assert.Single(_notifier.Sent);
        Assert.Equal("Server memory is 93% full", subject);
        Assert.Contains("13.0 GB of its 14.0 GB", text);
        Assert.Contains("ffmpeg: 3.0 GB", text);
    }

    [Fact]
    public async Task ZeroTurnsEachWarningOff()
    {
        var watch = Watch();
        for (var i = 0; i < 5; i++)
            await watch.CheckAsync(Reading(13, ("llama-server", 10)), 0, 0, Ct);
        Assert.Empty(_notifier.Sent);
    }

    [Fact]
    public async Task ARestart_DoesNotSendTheSameWarningAgain()
    {
        var path = Path.Combine(Path.GetTempPath(), $"memory-warnings-{Guid.NewGuid():N}.json");
        try
        {
            var leaking = Reading(13, ("llama-server", 8));
            await CheckTimes(new MemoryWatch(_notifier, NullLogger<MemoryWatch>.Instance, _clock, path), leaking);
            Assert.Equal(2, _notifier.Sent.Count);

            // The dashboard restarts (a deploy) half an hour later and the leak is still there.
            _clock.Now += TimeSpan.FromMinutes(30);
            await CheckTimes(new MemoryWatch(_notifier, NullLogger<MemoryWatch>.Instance, _clock, path), leaking, 10);
            Assert.Equal(2, _notifier.Sent.Count);

            // Twelve hours on, a fresh process does say it again.
            _clock.Now += TimeSpan.FromHours(12);
            await CheckTimes(new MemoryWatch(_notifier, NullLogger<MemoryWatch>.Instance, _clock, path), leaking);
            Assert.Equal(4, _notifier.Sent.Count);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public async Task ACorruptStateFile_CostsOneRepeat_NotTheWarning()
    {
        var path = Path.Combine(Path.GetTempPath(), $"memory-warnings-{Guid.NewGuid():N}.json");
        try
        {
            File.WriteAllText(path, "{ not json");
            var watch = new MemoryWatch(_notifier, NullLogger<MemoryWatch>.Instance, _clock, path);
            await CheckTimes(watch, Reading(10, ("llama-server", 8)));
            Assert.Single(_notifier.Sent);
            Assert.Contains("llama-server", File.ReadAllText(path));
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void ProcStatus_CountsSwapAsThatProgramsMemory_AndSkipsKernelThreads()
    {
        string[] llama = ["Name:\tllama-server", "VmPeak:\t 9000000 kB", "VmRSS:\t 4156124 kB", "VmSwap:\t 1000000 kB"];
        Assert.Equal(("llama-server", (4156124L + 1000000) * 1024), HostMemoryReader.ParseProcStatus(llama));

        string[] kthread = ["Name:\tkworker/0:1", "State:\tI (idle)"];
        Assert.Null(HostMemoryReader.ParseProcStatus(kthread));
    }

    [Fact]
    public void DotnetServicesAreNamedAfterTheirDll_EverythingElseByWhatWasRun()
    {
        Assert.Equal("SentinelCore.Web", HostMemoryReader.ProgramName(
            "/usr/bin/dotnet\0/opt/sentinelcore/SentinelCore.Web/bin/Release/net10.0/SentinelCore.Web.dll\0", "dotnet"));
        Assert.Equal("llama-server", HostMemoryReader.ProgramName(
            "/usr/local/lib/ollama/llama-server\0--model\0/blobs/sha256-aeda\0", "llama-server"));
        Assert.Equal("SentinelCore.Tests", HostMemoryReader.ProgramName(
            "/repo/SentinelCore.Tests/bin/Debug/net10.0/SentinelCore.Tests\0@/tmp/args\0", "SentinelCore.Te"));
        Assert.Equal("nginx", HostMemoryReader.ProgramName("nginx: worker process\0", "nginx"));
        Assert.Equal("kthreadd", HostMemoryReader.ProgramName("", "kthreadd"));
    }

    private sealed class ManualTime(DateTimeOffset now) : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = now;
        public override DateTimeOffset GetUtcNow() => Now;
    }

    private sealed class FakeNotifier : IOwnerNotifier
    {
        public List<(string Subject, string Text)> Sent { get; } = [];

        public Task<bool> SendAsync(string subject, string text, CancellationToken cancellationToken)
        {
            Sent.Add((subject, text));
            return Task.FromResult(true);
        }
    }
}
