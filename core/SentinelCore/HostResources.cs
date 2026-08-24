using System.Globalization;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace SentinelCore;

// Cumulative CPU tick counters for the whole machine. Deliberately raw totals rather than a
// percentage: every operating system exposes CPU time as an ever-increasing counter, and a
// percentage only means something once two readings are differenced. The caller keeps the
// previous reading and does that subtraction - see SystemStatsSampler.
public readonly record struct HostCpuTicks(ulong Busy, ulong Total);

public readonly record struct HostMemoryUsage(long UsedBytes, long TotalBytes);

// Machine-wide resource readings, as opposed to this process's own.
//
// The distinction is the entire point of this file. Server Health is read as "how is the box
// doing", and on this box the process asking the question is nearly idle while the work it
// causes - ffmpeg per camera, the vision backend - is what actually burns CPU and RAM. Reporting
// Environment.WorkingSet and this process's TotalProcessorTime under that heading meant the
// dashboard sat at 0% CPU and a flat ~400 MB no matter what the machine was doing.
//
// Each platform has one obvious right answer and they aren't shareable, hence three
// implementations. Any of them returning null means "couldn't read it here", and the caller
// falls back to the old process-scoped numbers rather than showing nothing.
public interface IHostResourceReader
{
    HostCpuTicks? ReadCpuTicks();

    HostMemoryUsage? ReadMemory();
}

public static class HostResourceReader
{
    public static IHostResourceReader? ForCurrentPlatform()
    {
        if (OperatingSystem.IsLinux()) return new LinuxHostResourceReader();
        if (OperatingSystem.IsWindows()) return new WindowsHostResourceReader();
        if (OperatingSystem.IsMacOS()) return new MacHostResourceReader();
        return null;
    }
}

// /proc/stat and /proc/meminfo - the same two files top(1) and free(1) read.
//
// Note that in a container both still describe the host, not the cgroup limit. That's the honest
// answer for the question being asked here (the cameras and ffmpeg share the host's CPU either
// way), and matching what an admin sees in `top` on the same box is worth more than matching a
// quota this app doesn't otherwise know about.
public sealed class LinuxHostResourceReader : IHostResourceReader
{
    public HostCpuTicks? ReadCpuTicks()
    {
        try
        {
            // Only the first line is needed and /proc/stat is long on a many-core box, so this
            // reads one line rather than slurping the file.
            using var reader = new StreamReader("/proc/stat");
            return ParseCpuTicks(reader.ReadLine());
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    public HostMemoryUsage? ReadMemory()
    {
        try
        {
            return ParseMemory(File.ReadLines("/proc/meminfo"));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    // Public rather than private so the parsers can be tested against captured /proc text without
    // needing a Linux box to run on - this project has no InternalsVisibleTo, same as
    // CameraDiscoveryService.ParseFfprobeOutput.
    //
    // The first line of /proc/stat, the "cpu" aggregate across every core:
    //   cpu  user nice system idle iowait irq softirq steal guest guest_nice
    // guest/guest_nice are already counted inside user/nice, so summing the first eight fields is
    // the total. Idle is idle + iowait - a core waiting on disk isn't doing work.
    public static HostCpuTicks? ParseCpuTicks(string? firstLine)
    {
        if (firstLine is null || !firstLine.StartsWith("cpu ", StringComparison.Ordinal)) return null;

        var fields = firstLine.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        ulong total = 0;
        ulong idle = 0;
        for (var i = 1; i < fields.Length && i <= 8; i++)
        {
            if (!ulong.TryParse(fields[i], NumberStyles.None, CultureInfo.InvariantCulture, out var value)) return null;
            total += value;
            if (i is 4 or 5) idle += value;
        }

        return total == 0 ? null : new HostCpuTicks(total - idle, total);
    }

    public static HostMemoryUsage? ParseMemory(IEnumerable<string> meminfoLines)
    {
        long totalKb = 0;
        long availableKb = -1;
        long freeKb = 0, buffersKb = 0, cachedKb = 0;

        foreach (var line in meminfoLines)
        {
            if (TryReadKb(line, "MemTotal:", out var value)) totalKb = value;
            else if (TryReadKb(line, "MemAvailable:", out value)) availableKb = value;
            else if (TryReadKb(line, "MemFree:", out value)) freeKb = value;
            else if (TryReadKb(line, "Buffers:", out value)) buffersKb = value;
            else if (TryReadKb(line, "Cached:", out value)) cachedKb = value;
        }

        if (totalKb <= 0) return null;

        // MemAvailable is the kernel's own estimate of what a new workload could claim without
        // swapping, which is what "used" should be measured against - counting page cache as used
        // would show any long-running box at ~100%. It has been there since Linux 3.14; the
        // fallback is only for kernels older than that.
        if (availableKb < 0) availableKb = freeKb + buffersKb + cachedKb;

        var used = Math.Clamp(totalKb - availableKb, 0, totalKb);
        return new HostMemoryUsage(used * 1024, totalKb * 1024);
    }

    private static bool TryReadKb(string line, string prefix, out long valueKb)
    {
        valueKb = 0;
        if (!line.StartsWith(prefix, StringComparison.Ordinal)) return false;

        // "MemTotal:       15727856 kB"
        var rest = line.AsSpan(prefix.Length).Trim();
        var space = rest.IndexOf(' ');
        if (space > 0) rest = rest[..space];
        return long.TryParse(rest, NumberStyles.None, CultureInfo.InvariantCulture, out valueKb);
    }
}

[SupportedOSPlatform("windows")]
public sealed class WindowsHostResourceReader : IHostResourceReader
{
    public HostCpuTicks? ReadCpuTicks()
    {
        if (!GetSystemTimes(out var idle, out var kernel, out var user)) return null;

        // Kernel time already includes idle time, so the busy total is (kernel + user) - idle.
        var idleTicks = ToTicks(idle);
        var total = ToTicks(kernel) + ToTicks(user);
        return total == 0 || idleTicks > total ? null : new HostCpuTicks(total - idleTicks, total);
    }

    public HostMemoryUsage? ReadMemory()
    {
        var status = new MEMORYSTATUSEX { dwLength = (uint)Marshal.SizeOf<MEMORYSTATUSEX>() };
        if (!GlobalMemoryStatusEx(ref status) || status.ullTotalPhys == 0) return null;

        var used = status.ullTotalPhys - status.ullAvailPhys;
        return new HostMemoryUsage((long)used, (long)status.ullTotalPhys);
    }

    private static ulong ToTicks(FILETIME time) => ((ulong)time.dwHighDateTime << 32) | time.dwLowDateTime;

    [StructLayout(LayoutKind.Sequential)]
    private struct FILETIME
    {
        public uint dwLowDateTime;
        public uint dwHighDateTime;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MEMORYSTATUSEX
    {
        public uint dwLength;
        public uint dwMemoryLoad;
        public ulong ullTotalPhys;
        public ulong ullAvailPhys;
        public ulong ullTotalPageFile;
        public ulong ullAvailPageFile;
        public ulong ullTotalVirtual;
        public ulong ullAvailVirtual;
        public ulong ullAvailExtendedVirtual;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetSystemTimes(out FILETIME idleTime, out FILETIME kernelTime, out FILETIME userTime);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GlobalMemoryStatusEx(ref MEMORYSTATUSEX buffer);
}

// The Mach host statistics that Activity Monitor and top(1) read on macOS. There is no /proc
// here and no equivalent sysctl, so this is the supported way to ask.
[SupportedOSPlatform("macos")]
public sealed class MacHostResourceReader : IHostResourceReader
{
    private const int HOST_CPU_LOAD_INFO = 3;
    private const int HOST_CPU_LOAD_INFO_COUNT = 4;
    private const int HOST_VM_INFO64 = 4;
    private const int HOST_VM_INFO64_COUNT = 38;
    private const int KERN_SUCCESS = 0;

    public HostCpuTicks? ReadCpuTicks()
    {
        // Four cumulative tick counters summed over every core: user, system, idle, nice.
        var info = new uint[HOST_CPU_LOAD_INFO_COUNT];
        var count = (uint)HOST_CPU_LOAD_INFO_COUNT;
        if (host_statistics(mach_host_self(), HOST_CPU_LOAD_INFO, info, ref count) != KERN_SUCCESS) return null;

        ulong total = 0;
        foreach (var value in info) total += value;
        var idle = (ulong)info[2];
        return total == 0 ? null : new HostCpuTicks(total - idle, total);
    }

    public HostMemoryUsage? ReadMemory()
    {
        var stats = default(VmStatistics64);
        var count = (uint)HOST_VM_INFO64_COUNT;
        if (host_statistics64(mach_host_self(), HOST_VM_INFO64, ref stats, ref count) != KERN_SUCCESS) return null;

        var total = GC.GetGCMemoryInfo().TotalAvailableMemoryBytes;
        if (total <= 0) return null;

        // Mirrors the Linux MemAvailable idea rather than Activity Monitor's "Memory Used"
        // headline: what a new workload could take without paging. Inactive and speculative
        // pages are reclaimable on demand, and purgeable pages are given up under pressure.
        long pageSize = Environment.SystemPageSize;
        var available = ((long)stats.FreeCount + stats.InactiveCount + stats.SpeculativeCount + stats.PurgeableCount) * pageSize;
        var used = Math.Clamp(total - available, 0, total);
        return new HostMemoryUsage(used, total);
    }

    // mach/vm_statistics.h. The natural_t/uint64_t mix is load-bearing - the field order and
    // widths below are what make the struct exactly HOST_VM_INFO64_COUNT 32-bit words long, which
    // is the size the kernel validates against before it writes anything.
    [StructLayout(LayoutKind.Sequential)]
    private struct VmStatistics64
    {
        public uint FreeCount;
        public uint ActiveCount;
        public uint InactiveCount;
        public uint WireCount;
        public ulong ZeroFillCount;
        public ulong Reactivations;
        public ulong PageIns;
        public ulong PageOuts;
        public ulong Faults;
        public ulong CowFaults;
        public ulong Lookups;
        public ulong Hits;
        public ulong Purges;
        public uint PurgeableCount;
        public uint SpeculativeCount;
        public ulong Decompressions;
        public ulong Compressions;
        public ulong SwapIns;
        public ulong SwapOuts;
        public uint CompressorPageCount;
        public uint ThrottledCount;
        public uint ExternalPageCount;
        public uint InternalPageCount;
        public ulong TotalUncompressedPagesInCompressor;
    }

    [DllImport("libSystem.dylib")]
    private static extern uint mach_host_self();

    [DllImport("libSystem.dylib")]
    private static extern int host_statistics(uint host, int flavor, [Out] uint[] info, ref uint count);

    [DllImport("libSystem.dylib")]
    private static extern int host_statistics64(uint host, int flavor, ref VmStatistics64 info, ref uint count);
}
