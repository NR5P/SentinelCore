namespace SentinelCore;

// Whether it's safe for this process to exit expecting something to bring it back up. The
// restart endpoints refuse to act unless this says yes: a supervised service (the shipped
// systemd units run Restart=always, RestartSec=5) comes back in seconds, while a process
// someone started by hand in a terminal would just be gone - turning "apply my settings" into
// an outage that ends only when someone gets back to a shell.
public static class RestartSupport
{
    // Restart:ForceAllowed covers supervisors this can't detect (Docker's restart policies,
    // launchd KeepAlive, a Windows service wrapper) - an explicit opt-in in configuration, so
    // it can also be set per-environment. SENTINELCORE_SUPERVISED=1 is the same opt-in for
    // setups where an environment variable is easier to inject than configuration.
    // INVOCATION_ID is set by systemd for every unit it starts, so the shipped units need no
    // opt-in at all.
    public static bool IsSupervised(IConfiguration configuration) =>
        configuration.GetValue<bool>("Restart:ForceAllowed")
        || Environment.GetEnvironmentVariable("INVOCATION_ID") is not null
        || Environment.GetEnvironmentVariable("SENTINELCORE_SUPERVISED") == "1";

    // Lets the 202 response and its logging flush before the host starts tearing down.
    public static void StopSoon(IHostApplicationLifetime lifetime) =>
        _ = Task.Run(async () =>
        {
            await Task.Delay(TimeSpan.FromMilliseconds(750));
            lifetime.StopApplication();
        });
}
