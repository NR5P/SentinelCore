using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using System.Diagnostics;
using Microsoft.Extensions.Options;

namespace SentinelCore;

// Rebooting the machine this runs on, from the dashboard or a phone, on whichever of the three
// operating systems it was installed on.
//
// This is a deliberately smaller promise than /api/system/restart next to it. That one bounces a
// process and leans on the supervisor to bring it straight back, which is why it can be safe by
// construction. This one takes the whole box down and trusts it to come up again on its own -
// something only the operating system can promise. So the feature is off by default, and turning
// it on grants nothing: the privilege to reboot has to be handed over outside the app, by a human
// at a shell. What the app can do is work out which grant is missing on this specific machine,
// hand over the exact commands for it, and then verify the grant landed - without rebooting to
// find out.
//
// Power-off is deliberately absent and should stay absent. A machine that is off cannot be turned
// back on by software, so a mis-tap ends in a drive to wherever the box lives. A reboot's worst
// case is the one any crash already has.
public sealed class HostRebootOptions
{
    public const string SectionName = "HostReboot";

    // Off by default. Note that this is only half of the story - see IHostRebootController: the
    // operating system also has to have been told to allow it, which this flag cannot do.
    public bool Enabled { get; init; }

    // The undo window. The wait is deliberately held here in-process rather than handed to
    // `shutdown -r +1` or `shutdown /r /t 60`: those disagree about units and granularity across
    // the three platforms, and cancelling them means a second privileged call that can fail on
    // its own. A timer we own cancels reliably, needs no privilege to cancel, and behaves
    // identically everywhere. The cost is that a crash inside the window means the reboot simply
    // doesn't happen, which is the right way for this to fail.
    [Range(0, 3600)]
    public int DelaySeconds { get; init; } = 60;
}

public static class HostRebootReadiness
{
    // The operating system will let this process reboot the machine right now.
    public const string Ready = "ready";

    // Everything is in place except the privilege itself, and Probe.SetupScript says how to
    // grant it.
    public const string SetupRequired = "setupRequired";

    // No amount of local configuration will make this work - a container, most usually, where
    // rebooting "the host" is not a thing this process can meaningfully ask for.
    public const string Unsupported = "unsupported";
}

// What a client needs to render the whole feature: whether it works, why not if not, and the
// literal commands to fix it. Deliberately carries prose rather than error codes - the audience
// for SetupScript is a person about to paste it into a terminal.
public sealed record HostRebootProbe
{
    public required string Platform { get; init; }
    public required string PlatformLabel { get; init; }
    public required string Readiness { get; init; }
    public required bool Enabled { get; init; }
    public required string RunningAs { get; init; }
    public required string Summary { get; init; }
    public required int DelaySeconds { get; init; }

    // Null when there is nothing to do - a client should show no command block at all rather
    // than an empty one.
    public string? SetupScript { get; init; }

    // The alternative route, for people who would rather not use the one above (polkit instead
    // of sudoers, a service account instead of LocalSystem).
    public string? SetupNote { get; init; }

    // Set only while a reboot is counting down, so a client that opens the screen mid-window
    // shows the countdown and its cancel button rather than the idle state.
    public DateTimeOffset? ScheduledFor { get; init; }

    // The same fact as ScheduledFor, as a number of seconds, and the one every client actually
    // renders from. Working it out here rather than letting each client subtract ScheduledFor
    // from its own clock means a phone or TV that is a few minutes out of sync still shows a
    // truthful countdown - and this is the one number on that screen where being confidently
    // wrong would matter.
    public int? SecondsRemaining => HostRebootTiming.SecondsUntil(ScheduledFor);
}

public sealed record HostRebootOutcome(bool Ok, string Message, DateTimeOffset? ScheduledFor = null)
{
    // Serialized alongside Message, and only when something went wrong. Every client already has
    // exactly one helper for pulling a human-readable reason out of a non-2xx body, and all of
    // them look for "error" - teaching four clients a second field name is a worse trade than
    // writing this string twice.
    public string? Error => Ok ? null : Message;

    public int? SecondsRemaining => HostRebootTiming.SecondsUntil(ScheduledFor);
}

internal static class HostRebootTiming
{
    public static int? SecondsUntil(DateTimeOffset? due) => due is { } moment
        ? Math.Max(0, (int)Math.Round((moment - DateTimeOffset.UtcNow).TotalSeconds))
        : null;
}

// The per-platform half. Probing and rebooting are the only two things that differ between
// Linux, macOS and Windows; everything above and below this interface is shared.
public interface IHostRebootController
{
    Task<HostRebootProbe> ProbeAsync(CancellationToken cancellationToken);

    // Reboot now, with no delay of its own - HostRebootScheduler owns the undo window.
    Task<HostRebootOutcome> ExecuteAsync(CancellationToken cancellationToken);
}

internal sealed record HostCommandResult(bool Started, int ExitCode, string StandardOutput, string StandardError)
{
    public bool Succeeded => Started && ExitCode == 0;

    public string Diagnostic =>
        string.IsNullOrWhiteSpace(StandardError) ? StandardOutput.Trim() : StandardError.Trim();
}

// Runs one external command and waits for it, with a timeout so a probe can never hang the
// request that asked for it. Every failure - missing binary, non-zero exit, timeout - comes back
// as data rather than an exception, because on this path every one of them is a legitimate
// answer to "can this machine reboot itself?" rather than a fault.
internal static class HostCommand
{
    public static async Task<HostCommandResult> RunAsync(
        string fileName,
        IReadOnlyList<string> arguments,
        CancellationToken cancellationToken)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = fileName,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        foreach (var argument in arguments)
            startInfo.ArgumentList.Add(argument);

        Process? process = null;
        try
        {
            process = Process.Start(startInfo);
            if (process is null)
                return new HostCommandResult(false, -1, string.Empty, $"Could not start {fileName}.");

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(15));

            var standardOutput = process.StandardOutput.ReadToEndAsync(timeout.Token);
            var standardError = process.StandardError.ReadToEndAsync(timeout.Token);
            await process.WaitForExitAsync(timeout.Token);

            return new HostCommandResult(true, process.ExitCode, await standardOutput, await standardError);
        }
        catch (Win32Exception ex)
        {
            // The usual shape of "sudo isn't installed" or "shutdown isn't where we looked".
            return new HostCommandResult(false, -1, string.Empty, ex.Message);
        }
        catch (InvalidOperationException ex)
        {
            return new HostCommandResult(false, -1, string.Empty, ex.Message);
        }
        catch (OperationCanceledException)
        {
            try { process?.Kill(entireProcessTree: true); }
            catch (InvalidOperationException) { /* already gone */ }
            catch (NotSupportedException) { /* not killable here; nothing further to try */ }
            return new HostCommandResult(false, -1, string.Empty, $"{fileName} did not finish within 15 seconds.");
        }
        finally
        {
            process?.Dispose();
        }
    }
}

// Linux and macOS differ only in where `shutdown` lives and what the setup advice says, so the
// sudo reasoning - which is the part with the sharp edges - is written once here.
//
// The pivotal detail is that the probe checks the *exact* argv the reboot will later use.
// `sudo -l /sbin/shutdown` asks a different question than `sudo -l /sbin/shutdown -r now`, and
// against the narrow single-command rule this feature recommends, only the second one answers
// truthfully. Getting that wrong would produce the precise failure this whole design exists to
// prevent: a green "ready" badge that turns out to be wrong at the one moment it matters.
internal abstract class UnixHostRebootController : IHostRebootController
{
    private static readonly string[] RebootArguments = ["-r", "now"];

    protected abstract string Platform { get; }
    protected abstract string PlatformLabel { get; }
    protected abstract IReadOnlyList<string> ShutdownBinaryCandidates { get; }
    protected abstract string BuildSetupScript(string user, string shutdownBinary);
    protected abstract string SetupNote { get; }

    private readonly IOptionsMonitor<HostRebootOptions> _options;

    protected UnixHostRebootController(IOptionsMonitor<HostRebootOptions> options) => _options = options;

    private string? FindShutdownBinary() => ShutdownBinaryCandidates.FirstOrDefault(path => File.Exists(path));

    private static bool IsRoot => string.Equals(Environment.UserName, "root", StringComparison.Ordinal);

    public async Task<HostRebootProbe> ProbeAsync(CancellationToken cancellationToken)
    {
        var user = Environment.UserName;

        HostRebootProbe Build(string readiness, string summary, string? script = null, string? note = null) => new()
        {
            Platform = Platform,
            PlatformLabel = PlatformLabel,
            Readiness = readiness,
            Enabled = _options.CurrentValue.Enabled,
            RunningAs = user,
            Summary = summary,
            DelaySeconds = _options.CurrentValue.DelaySeconds,
            SetupScript = script,
            SetupNote = note
        };

        if (ContainerDetection.InsideContainer())
        {
            return Build(
                HostRebootReadiness.Unsupported,
                "This copy is running inside a container, which can't reboot the machine underneath it. " +
                "Reboot the host from the host itself, or from whatever manages the container.");
        }

        var shutdown = FindShutdownBinary();
        if (shutdown is null)
        {
            return Build(
                HostRebootReadiness.Unsupported,
                $"No shutdown command was found on this {PlatformLabel} install (looked in " +
                $"{string.Join(", ", ShutdownBinaryCandidates)}), so there's nothing for this to call.");
        }

        if (IsRoot)
        {
            return Build(
                HostRebootReadiness.Ready,
                $"Running as root on {PlatformLabel}, so no extra permission is needed. Nothing to set up.");
        }

        var probe = await HostCommand.RunAsync("sudo", ["-n", "-l", shutdown, .. RebootArguments], cancellationToken);
        if (probe.Succeeded)
        {
            return Build(
                HostRebootReadiness.Ready,
                $"{user} is allowed to run {shutdown} -r now without a password. Ready to reboot.");
        }

        var reason = probe.Started
            ? $"{user} isn't allowed to run {shutdown} -r now without a password yet."
            : $"Couldn't ask sudo whether {user} may reboot ({probe.Diagnostic}).";

        return Build(
            HostRebootReadiness.SetupRequired,
            reason + " Run the commands below on the server once, as someone who can use sudo, then check again.",
            BuildSetupScript(user, shutdown),
            SetupNote);
    }

    public async Task<HostRebootOutcome> ExecuteAsync(CancellationToken cancellationToken)
    {
        var shutdown = FindShutdownBinary();
        if (shutdown is null)
            return new HostRebootOutcome(false, "No shutdown command was found on this machine.");

        var result = IsRoot
            ? await HostCommand.RunAsync(shutdown, RebootArguments, cancellationToken)
            : await HostCommand.RunAsync("sudo", ["-n", shutdown, .. RebootArguments], cancellationToken);

        // A reboot that is actually happening often kills this process before the child is
        // reaped, so a timeout here is at least as likely to mean success as failure. Only a
        // clean non-zero exit is reported as a failure.
        if (result.Started && result.ExitCode != 0)
            return new HostRebootOutcome(false, $"The reboot command failed: {result.Diagnostic}");

        if (!result.Started)
            return new HostRebootOutcome(false, $"Could not run the reboot command: {result.Diagnostic}");

        return new HostRebootOutcome(true, "The machine is rebooting.");
    }
}

internal sealed class LinuxHostRebootController(IOptionsMonitor<HostRebootOptions> options)
    : UnixHostRebootController(options)
{
    private static readonly string[] Candidates = ["/usr/sbin/shutdown", "/sbin/shutdown", "/usr/bin/shutdown"];

    protected override string Platform => "linux";
    protected override string PlatformLabel => "Linux";
    protected override IReadOnlyList<string> ShutdownBinaryCandidates => Candidates;

    protected override string BuildSetupScript(string user, string shutdownBinary) =>
        $"""
        sudo mkdir -p /etc/sudoers.d
        printf '%s\n' '{user} ALL=(root) NOPASSWD: {shutdownBinary} -r now' | sudo tee /etc/sudoers.d/sentinelcore-reboot >/dev/null
        sudo chmod 440 /etc/sudoers.d/sentinelcore-reboot
        sudo visudo -c -f /etc/sudoers.d/sentinelcore-reboot
        """;

    // sudoers rather than polkit as the default advice on purpose. polkit is the more native
    // answer on systemd and grants less, but org.freedesktop.login1.reboot quietly stops being
    // the action that gets checked as soon as someone else is logged in - it becomes
    // reboot-multiple-sessions - which turns into a rule that works on the day you write it and
    // mysteriously doesn't months later. The sudoers line above grants exactly one argv and is
    // revoked by deleting one file.
    protected override string SetupNote =>
        "Prefer polkit? Create /etc/polkit-1/rules.d/49-sentinelcore-reboot.rules granting " +
        "org.freedesktop.login1.reboot to this user - but note that polkit checks " +
        "org.freedesktop.login1.reboot-multiple-sessions instead whenever anyone else is logged " +
        "in, so grant both or the button works only some of the time. To undo the sudoers route, " +
        "delete /etc/sudoers.d/sentinelcore-reboot.";
}

internal sealed class MacHostRebootController(IOptionsMonitor<HostRebootOptions> options)
    : UnixHostRebootController(options)
{
    private static readonly string[] Candidates = ["/sbin/shutdown"];

    protected override string Platform => "macos";
    protected override string PlatformLabel => "macOS";
    protected override IReadOnlyList<string> ShutdownBinaryCandidates => Candidates;

    // The installed LaunchDaemons set <key>UserName</key> to the install user rather than running
    // as root, which is why this is needed on macOS at all. /etc is a symlink to /private/etc and
    // the stock sudoers file already carries "#includedir /private/etc/sudoers.d", so a file
    // dropped here is picked up with no further edit.
    protected override string BuildSetupScript(string user, string shutdownBinary) =>
        $"""
        sudo mkdir -p /etc/sudoers.d
        printf '%s\n' '{user} ALL=(root) NOPASSWD: {shutdownBinary} -r now' | sudo tee /etc/sudoers.d/sentinelcore-reboot >/dev/null
        sudo chmod 440 /etc/sudoers.d/sentinelcore-reboot
        sudo visudo -c -f /etc/sudoers.d/sentinelcore-reboot
        """;

    protected override string SetupNote =>
        "You'll be asked for your password by sudo, and on newer macOS the Terminal may also ask " +
        "for Full Disk Access before it can write to /etc/sudoers.d. To undo this later, delete " +
        "/etc/sudoers.d/sentinelcore-reboot.";
}

// Windows is the one platform where this usually needs no setup at all: install-windows.ps1
// registers the services with New-Service and no -Credential, so they run as LocalSystem, which
// holds SeShutdownPrivilege. Shelling out to shutdown.exe rather than calling
// InitiateSystemShutdownEx is deliberate - shutdown.exe enables the privilege in its own token,
// so there's no P/Invoke and no AdjustTokenPrivileges dance to get wrong.
internal sealed class WindowsHostRebootController(IOptionsMonitor<HostRebootOptions> options)
    : IHostRebootController
{
    // /f because the entire point of this feature is recovering a box nobody is standing next to.
    // Without it a single application refusing to close leaves the reboot pending behind a dialog
    // no one will ever see, which is a worse outcome than closing something uncleanly on what is
    // by definition a server.
    private static readonly string[] RebootArguments =
        ["/r", "/t", "0", "/f", "/c", "Rebooting at the request of Sentinel Core."];

    private static readonly string[] PrivilegeArguments = ["/priv"];

    public async Task<HostRebootProbe> ProbeAsync(CancellationToken cancellationToken)
    {
        var user = Environment.UserName;

        HostRebootProbe Build(string readiness, string summary, string? script = null, string? note = null) => new()
        {
            Platform = "windows",
            PlatformLabel = "Windows",
            Readiness = readiness,
            Enabled = options.CurrentValue.Enabled,
            RunningAs = user,
            Summary = summary,
            DelaySeconds = options.CurrentValue.DelaySeconds,
            SetupScript = script,
            SetupNote = note
        };

        // The privilege's internal name is not localised even though its display name is, so
        // matching on "SeShutdownPrivilege" works on a Windows install in any language.
        var privileges = await HostCommand.RunAsync("whoami.exe", PrivilegeArguments, cancellationToken);
        if (privileges.Succeeded &&
            privileges.StandardOutput.Contains("SeShutdownPrivilege", StringComparison.OrdinalIgnoreCase))
        {
            return Build(
                HostRebootReadiness.Ready,
                $"This service runs as {user}, which holds SeShutdownPrivilege. Nothing to set up.");
        }

        var reason = privileges.Started
            ? $"The account this service runs as ({user}) doesn't hold SeShutdownPrivilege, so it can't reboot Windows."
            : $"Couldn't read this account's privileges ({privileges.Diagnostic}).";

        return Build(
            HostRebootReadiness.SetupRequired,
            reason + " Run the commands below in an elevated PowerShell or Command Prompt on the server, then check again.",
            """
            sc.exe config SentinelCoreBridge obj= LocalSystem
            sc.exe stop SentinelCoreBridge
            sc.exe start SentinelCoreBridge
            """,
            "The space after obj= is required - sc.exe treats it as part of the syntax. Would rather " +
            "keep the service on its own account? Grant that account the \"Shut down the system\" " +
            "right instead, under Local Security Policy (secpol.msc) -> Local Policies -> User " +
            "Rights Assignment, then restart the service.");
    }

    public async Task<HostRebootOutcome> ExecuteAsync(CancellationToken cancellationToken)
    {
        var result = await HostCommand.RunAsync("shutdown.exe", RebootArguments, cancellationToken);

        if (result.Started && result.ExitCode != 0)
            return new HostRebootOutcome(false, $"The reboot command failed: {result.Diagnostic}");

        if (!result.Started)
            return new HostRebootOutcome(false, $"Could not run the reboot command: {result.Diagnostic}");

        return new HostRebootOutcome(true, "The machine is rebooting.");
    }
}

// Anything that isn't Linux, macOS or Windows. Answering honestly beats printing commands that
// were guessed and won't work.
internal sealed class UnsupportedHostRebootController(IOptionsMonitor<HostRebootOptions> options) : IHostRebootController
{
    public Task<HostRebootProbe> ProbeAsync(CancellationToken cancellationToken) =>
        Task.FromResult(new HostRebootProbe
        {
            Platform = "unsupported",
            PlatformLabel = "this operating system",
            Readiness = HostRebootReadiness.Unsupported,
            Enabled = options.CurrentValue.Enabled,
            RunningAs = Environment.UserName,
            Summary = "Rebooting the machine is only wired up for Linux, macOS and Windows, and this isn't one of them.",
            DelaySeconds = options.CurrentValue.DelaySeconds
        });

    public Task<HostRebootOutcome> ExecuteAsync(CancellationToken cancellationToken) =>
        Task.FromResult(new HostRebootOutcome(false, "Rebooting isn't supported on this operating system."));
}

internal static class ContainerDetection
{
    private static readonly string[] Markers = ["docker", "containerd", "lxc", "kubepods"];

    // Cheap file checks rather than systemd-detect-virt, which isn't present everywhere this
    // might run and would mean spawning a process to answer a question three files already
    // answer. False negatives are the safe direction here: the worst case is that the probe goes
    // on to ask sudo, which then says no, and the user gets a setup script instead of a clean
    // "not supported". False positives would be worse - they'd disable a feature that works.
    public static bool InsideContainer()
    {
        if (File.Exists("/.dockerenv") || File.Exists("/run/.containerenv"))
            return true;

        // Set by Podman and by systemd-nspawn.
        if (!string.IsNullOrEmpty(Environment.GetEnvironmentVariable("container")))
            return true;

        try
        {
            if (!File.Exists("/proc/1/cgroup"))
                return false;

            var cgroup = File.ReadAllText("/proc/1/cgroup");
            return Markers.Any(marker => cgroup.Contains(marker, StringComparison.OrdinalIgnoreCase));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }
}

public static class HostRebootControllerFactory
{
    public static IHostRebootController Create(IOptionsMonitor<HostRebootOptions> options) =>
        OperatingSystem.IsLinux() ? new LinuxHostRebootController(options)
        : OperatingSystem.IsMacOS() ? new MacHostRebootController(options)
        : OperatingSystem.IsWindows() ? new WindowsHostRebootController(options)
        : new UnsupportedHostRebootController(options);
}

// Owns the undo window, and is the only thing that ever calls IHostRebootController.ExecuteAsync.
//
// Two refusals matter more than anything else here, and both are checked at the moment of the
// request rather than trusted from earlier: the feature has to be switched on, and the probe has
// to still say ready. The second one is the important one - a settings toggle saved months ago
// says nothing about whether the sudoers file survived the last OS upgrade, and the difference
// between finding that out here and finding it out from a countdown that silently does nothing
// is the difference between a useful tool and a dangerous one.
public sealed class HostRebootScheduler(
    IHostRebootController controller,
    IOptionsMonitor<HostRebootOptions> options,
    ILogger<HostRebootScheduler> logger)
{
    private readonly object _gate = new();
    private CancellationTokenSource? _pending;
    private DateTimeOffset? _scheduledFor;

    // Set once the reboot command has actually been handed to the operating system. From that
    // moment cancelling is a lie we refuse to tell - nothing in this process can call it back.
    private bool _committed;

    public DateTimeOffset? ScheduledFor
    {
        get { lock (_gate) { return _scheduledFor; } }
    }

    public async Task<HostRebootOutcome> ScheduleAsync(CancellationToken cancellationToken)
    {
        var settings = options.CurrentValue;
        if (!settings.Enabled)
        {
            return new HostRebootOutcome(
                false,
                "Rebooting this machine is switched off. Turn on Host Reboot in Settings first.");
        }

        // Re-probed every time, deliberately - see the class comment.
        var probe = await controller.ProbeAsync(cancellationToken);
        if (!string.Equals(probe.Readiness, HostRebootReadiness.Ready, StringComparison.Ordinal))
            return new HostRebootOutcome(false, probe.Summary);

        var delay = TimeSpan.FromSeconds(Math.Clamp(settings.DelaySeconds, 0, 3600));

        CancellationTokenSource source;
        DateTimeOffset dueAt;
        lock (_gate)
        {
            if (_pending is not null)
                return new HostRebootOutcome(true, "A reboot is already counting down.", _scheduledFor);

            source = new CancellationTokenSource();
            dueAt = DateTimeOffset.UtcNow + delay;
            _pending = source;
            _scheduledFor = dueAt;
            _committed = false;
        }

        logger.LogWarning(
            "Host reboot requested - rebooting in {Seconds}s unless cancelled first.",
            (int)delay.TotalSeconds);

        _ = Task.Run(async () =>
        {
            try
            {
                try
                {
                    await Task.Delay(delay, source.Token);
                }
                catch (OperationCanceledException)
                {
                    logger.LogWarning("Host reboot cancelled before the window elapsed.");
                    return;
                }

                lock (_gate) { _committed = true; }

                var outcome = await controller.ExecuteAsync(CancellationToken.None);
                if (outcome.Ok)
                {
                    logger.LogWarning("Reboot command accepted by the operating system - going down.");
                    return;
                }

                // Worth an error rather than a warning: the operator believes the machine is
                // rebooting, and it isn't. Clearing the pending state puts the button back.
                logger.LogError("Host reboot did not happen: {Message}", outcome.Message);
                lock (_gate)
                {
                    if (ReferenceEquals(_pending, source))
                    {
                        _pending = null;
                        _scheduledFor = null;
                        _committed = false;
                    }
                }
            }
            finally
            {
                source.Dispose();
            }
        }, CancellationToken.None);

        return new HostRebootOutcome(
            true,
            delay > TimeSpan.Zero
                ? $"Rebooting in {(int)delay.TotalSeconds} seconds. Cancel before then to call it off."
                : "Rebooting now.",
            dueAt);
    }

    public HostRebootOutcome Cancel()
    {
        CancellationTokenSource? pending;
        lock (_gate)
        {
            if (_committed)
                return new HostRebootOutcome(false, "Too late to cancel - the machine is already going down.");

            pending = _pending;
            _pending = null;
            _scheduledFor = null;
        }

        if (pending is null)
            return new HostRebootOutcome(false, "No reboot is scheduled.");

        try
        {
            pending.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // Raced with the window elapsing; _committed above is the authoritative answer and
            // there is nothing left to cancel either way.
        }

        return new HostRebootOutcome(true, "Reboot cancelled.");
    }
}
