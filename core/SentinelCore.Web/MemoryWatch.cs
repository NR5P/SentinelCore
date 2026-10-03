using System.Diagnostics;
using System.Globalization;
using SentinelCore;

namespace SentinelCore.Web;

// One program's share of memory: everything running under the same name added together (six
// camera ffmpegs are one line), with what it has pushed out to swap counted too - a leak that has
// spilled into swap is still that program's memory, and on Linux it is what the OOM killer weighs.
public sealed record ProgramMemory(string Name, long Bytes, int Count);

public sealed record MemoryReading(long UsedBytes, long TotalBytes, IReadOnlyList<ProgramMemory> Programs)
{
    public double UsedPercent => TotalBytes <= 0 ? 0 : UsedBytes * 100.0 / TotalBytes;
}

public interface IMemoryReader
{
    MemoryReading? Read();
}

// Warns the owner - push to the apps, email, Telegram, through OwnerNotifier - while there is
// still time to act, instead of finding out from the kernel's OOM killer. Found the hard way on
// the media box (2026-10): Ollama's runner grew from 4 GB to ~12 GB of a 14 GB machine every few
// hours, the kernel killed it 34 times in a month, camera AI quietly failed each time, and deploys
// crashed because there was nothing left for the build.
//
// Two separate signals, because they arrive at different times:
//  - one program using more than ProcessMemoryWarningPercent of the machine. This is the early
//    one: a leak crosses half of memory hours before the machine as a whole runs out, and the
//    message can name the culprit.
//  - the machine as a whole above MemoryWarningPercent. The late one, for when no single program
//    is to blame.
// Each needs ConsecutiveChecks readings in a row (a build or a big transcode is a brief spike, not
// a problem) and is sent at most once per RepeatAfter, whether it stays high or keeps coming back.
public sealed class MemoryWatch(IOwnerNotifier notifier, ILogger<MemoryWatch> logger, TimeProvider? clock = null)
{
    public const int ConsecutiveChecks = 3;
    public static readonly TimeSpan RepeatAfter = TimeSpan.FromHours(12);
    private const int ProgramsListed = 5;

    private readonly TimeProvider _clock = clock ?? TimeProvider.System;
    private readonly Signal _machine = new();
    // Keyed by program name. Pruned to the programs currently over the line or still counting.
    private readonly Dictionary<string, Signal> _programs = new(StringComparer.Ordinal);

    private sealed class Signal
    {
        public int HighChecks;
        public DateTimeOffset? LastSent;
    }

    public async Task CheckAsync(MemoryReading reading, int machinePercent, int programPercent, CancellationToken cancellationToken)
    {
        if (reading.TotalBytes <= 0) return;

        if (programPercent > 0)
        {
            foreach (var program in reading.Programs)
            {
                var percent = program.Bytes * 100.0 / reading.TotalBytes;
                if (!_programs.TryGetValue(program.Name, out var signal))
                {
                    if (percent < programPercent) continue;
                    _programs[program.Name] = signal = new Signal();
                }
                if (Observe(signal, percent, programPercent))
                    await SendAsync(signal, ProgramWarning(program, reading), cancellationToken);
            }
            // A program that has exited or dropped back under has nothing left to remember -
            // except when it was last warned about, so a leak that gets killed and regrows every
            // few hours does not page every few hours.
            var running = reading.Programs.Select(p => p.Name).ToHashSet(StringComparer.Ordinal);
            foreach (var name in _programs.Keys.ToList())
            {
                var signal = _programs[name];
                if (!running.Contains(name)) signal.HighChecks = 0;
                if (signal.HighChecks == 0 && (signal.LastSent is not { } sent || _clock.GetUtcNow() - sent >= RepeatAfter))
                    _programs.Remove(name);
            }
        }

        if (machinePercent > 0 && Observe(_machine, reading.UsedPercent, machinePercent))
            await SendAsync(_machine, MachineWarning(reading), cancellationToken);
    }

    // True when it is time to tell the owner.
    private bool Observe(Signal signal, double percent, int threshold)
    {
        if (percent < threshold)
        {
            signal.HighChecks = 0;
            return false;
        }

        signal.HighChecks++;
        if (signal.HighChecks < ConsecutiveChecks) return false;
        // Whether it stayed high or recovered and came back, one warning per RepeatAfter: a leak
        // that is killed and regrows every few hours is the same trouble each time.
        return signal.LastSent is not { } sent || _clock.GetUtcNow() - sent >= RepeatAfter;
    }

    private async Task SendAsync(Signal signal, (string Subject, string Text) message, CancellationToken cancellationToken)
    {
        logger.LogWarning("Memory warning: {Subject}", message.Subject);
        // Counted as sent either way: with no channel set up there is nobody to repeat it to, and
        // the log line above is the record.
        signal.LastSent = _clock.GetUtcNow();
        if (!await notifier.SendAsync(message.Subject, message.Text, cancellationToken))
            logger.LogWarning("Memory warning: no email, Telegram or push is set up to say so");
    }

    public static (string Subject, string Text) ProgramWarning(ProgramMemory program, MemoryReading reading)
    {
        var percent = program.Bytes * 100.0 / reading.TotalBytes;
        var subject = $"{program.Name} is using {Gb(program.Bytes)} of memory";
        var text =
            $"{Describe(program)} is using {Gb(program.Bytes)}, {percent:0}% of the server's {Gb(reading.TotalBytes)}. " +
            "If it keeps growing the server will run out of memory, and Linux will start killing programs - " +
            "the camera AI, recordings, or Sentinel Core itself. " + Advice(program.Name) +
            "\n\n" + TopPrograms(reading);
        return (subject, text);
    }

    public static (string Subject, string Text) MachineWarning(MemoryReading reading)
    {
        var subject = $"Server memory is {reading.UsedPercent:0}% full";
        var text =
            $"The server is using {Gb(reading.UsedBytes)} of its {Gb(reading.TotalBytes)} of memory. " +
            "If it runs out, Linux will start killing programs - the camera AI, recordings, or Sentinel Core itself." +
            "\n\n" + TopPrograms(reading);
        return (subject, text);
    }

    private static string Describe(ProgramMemory program) =>
        program.Count > 1 ? $"{program.Name} ({program.Count} copies)" : program.Name;

    // The one culprit already known by name gets the fix spelled out.
    private static string Advice(string name) =>
        name is "llama-server" or "ollama"
            ? "This is Ollama, the AI that reads camera snapshots; its memory tends to creep up the longer it runs. " +
              "Restarting it frees it: sudo systemctl restart ollama"
            : "Restarting it usually frees the memory.";

    private static string TopPrograms(MemoryReading reading)
    {
        var lines = reading.Programs.OrderByDescending(p => p.Bytes).Take(ProgramsListed)
            .Select(p => $"  {Describe(p)}: {Gb(p.Bytes)}");
        return "Biggest users of memory:\n" + string.Join("\n", lines);
    }

    public static string Gb(long bytes) =>
        (bytes / (1024.0 * 1024 * 1024)).ToString("0.0", CultureInfo.InvariantCulture) + " GB";
}

// The machine's memory from HostResourceReader (the same numbers Server Health shows) and every
// program's from /proc on Linux, or Process elsewhere.
public sealed class HostMemoryReader : IMemoryReader
{
    private readonly IHostResourceReader? _host = HostResourceReader.ForCurrentPlatform();

    public MemoryReading? Read()
    {
        if (_host?.ReadMemory() is not { } memory) return null;
        var programs = OperatingSystem.IsLinux() ? ReadLinuxPrograms() : ReadPrograms();
        return new MemoryReading(memory.UsedBytes, memory.TotalBytes, programs);
    }

    private static List<ProgramMemory> ReadLinuxPrograms()
    {
        var found = new List<(string Name, long Bytes)>();
        foreach (var directory in Directory.EnumerateDirectories("/proc"))
        {
            var pid = Path.GetFileName(directory);
            if (pid.Length == 0 || !char.IsAsciiDigit(pid[0])) continue;
            try
            {
                var status = File.ReadAllLines(Path.Combine(directory, "status"));
                var cmdline = File.ReadAllText(Path.Combine(directory, "cmdline"));
                if (ParseProcStatus(status) is not { } entry) continue;
                found.Add((ProgramName(cmdline, entry.Name), entry.Bytes));
            }
            // Processes come and go while this walks /proc; one that vanished mid-read is skipped.
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
        return Group(found);
    }

    private static List<ProgramMemory> ReadPrograms()
    {
        var found = new List<(string Name, long Bytes)>();
        foreach (var process in Process.GetProcesses())
        {
            using (process)
            {
                try { found.Add((process.ProcessName, process.WorkingSet64)); }
                catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception) { }
            }
        }
        return Group(found);
    }

    private static List<ProgramMemory> Group(List<(string Name, long Bytes)> found) =>
        found.Where(p => p.Bytes > 0)
            .GroupBy(p => p.Name, StringComparer.Ordinal)
            .Select(g => new ProgramMemory(g.Key, g.Sum(p => p.Bytes), g.Count()))
            .OrderByDescending(p => p.Bytes)
            .ToList();

    // /proc/<pid>/status: Name, plus VmRSS (in memory) and VmSwap (pushed out to swap), both in kB.
    // Kernel threads have no VmRSS and are skipped.
    public static (string Name, long Bytes)? ParseProcStatus(IEnumerable<string> lines)
    {
        string? name = null;
        long? rssKb = null;
        long swapKb = 0;
        foreach (var line in lines)
        {
            if (line.StartsWith("Name:", StringComparison.Ordinal)) name = line[5..].Trim();
            else if (line.StartsWith("VmRSS:", StringComparison.Ordinal)) rssKb = Kb(line[6..]);
            else if (line.StartsWith("VmSwap:", StringComparison.Ordinal)) swapKb = Kb(line[7..]) ?? 0;
        }
        return name is null || rssKb is not { } rss ? null : (name, (rss + swapKb) * 1024);
    }

    private static long? Kb(string value)
    {
        var text = value.Trim();
        var space = text.IndexOf(' ');
        if (space > 0) text = text[..space];
        return long.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var kb) ? kb : null;
    }

    // The name a person would recognise. status's Name is cut to 15 characters ("SentinelCore.Te")
    // and every .NET app is just "dotnet", which says nothing about which of the three services
    // is growing - so a dotnet process is named after its .dll, anything else after the full name
    // of what was run.
    public static string ProgramName(string cmdline, string statusName)
    {
        var args = cmdline.Split('\0', StringSplitOptions.RemoveEmptyEntries);
        if (args.Length == 0) return statusName;
        var program = Path.GetFileName(args[0]);
        if (program == "dotnet")
        {
            var dll = args.Skip(1).FirstOrDefault(a => a.EndsWith(".dll", StringComparison.OrdinalIgnoreCase));
            if (dll is not null) return Path.GetFileNameWithoutExtension(dll);
        }
        // A process that rewrote its own argv (nginx: worker process) or a script interpreter
        // reads better under the kernel's name.
        return program.Length == 0 || program.Contains(' ') ? statusName : program;
    }
}

public sealed class MemoryWatchWorker(MemoryWatch watch, IMemoryReader reader, AppSettingsFile settings, ILogger<MemoryWatchWorker> logger) : BackgroundService
{
    public static readonly TimeSpan CheckEvery = TimeSpan.FromMinutes(1);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var options = await settings.GetSectionAsync<SelfMonitoringSettingsDto>("SelfMonitoring", stoppingToken) ?? new SelfMonitoringSettingsDto();
                if (options.Enabled && (options.MemoryWarningPercent > 0 || options.ProcessMemoryWarningPercent > 0) && reader.Read() is { } reading)
                    await watch.CheckAsync(reading, options.MemoryWarningPercent, options.ProcessMemoryWarningPercent, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception ex) { logger.LogWarning(ex, "The memory check failed"); }
            try { await Task.Delay(CheckEvery, stoppingToken); }
            catch (OperationCanceledException) { break; }
        }
    }
}
