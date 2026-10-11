using System.Diagnostics;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace LicenseGenerator.Gui;

public sealed class RelayConnectionException(string message) : Exception(message);

// Reads and edits Relay:BlockedLicenseIds in the push relay's appsettings.json on the machine that
// runs it, through this Mac's own ssh - whatever keys and ~/.ssh/config already get you onto that
// box, and nothing stored here. BatchMode means a missing key fails with a message instead of
// hanging on a password prompt nobody can see.
//
// The relay re-reads that file when it changes, so a save here is the whole of blocking - no
// restart, no deploy. See RelayOptions.BlockedLicenseIds and docs/PUSH_RELAY.md.
public sealed partial class RelayBlockList
{
    // Where the relay runs. All three are settings in the window (remembered in AppState), so
    // moving the relay to another host is three fields, not a rebuild - see "Moving it to a
    // proper host later" in docs/PUSH_RELAY.md. There is no default ssh target: it is whatever
    // user@host reaches your relay box, entered once and then remembered.
    public const string DefaultSshTarget = "";
    public const string DefaultConfigPath = "/opt/sentinelcore/core/SentinelCore.PushRelay/appsettings.json";
    public const string DefaultServiceName = "sentinelcore-push-relay";
    private static readonly TimeSpan CommandTimeout = TimeSpan.FromSeconds(20);

    private static readonly JsonSerializerOptions WriteOptions = new()
    {
        WriteIndented = true,
        // The file is hand-edited on the box; keep it looking hand-written rather than \u-escaped.
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    private readonly string _sshTarget;
    private readonly string _configPath;
    private readonly string _serviceName;

    // serviceName is the systemd unit whose journal confirms a reload; empty for a relay that runs
    // some other way (a container, another init system), where the save is simply not confirmed.
    public RelayBlockList(string sshTarget, string configPath, string serviceName)
    {
        _sshTarget = sshTarget.Trim();
        _configPath = configPath.Trim();
        _serviceName = serviceName.Trim();
        if (_serviceName.Length > 0 && !ServiceNamePattern().IsMatch(_serviceName))
            throw new RelayConnectionException("The relay's service name can only contain letters, digits and . _ @ - characters.");
        // Handed to ssh as its own argument, so this is about ssh reading it as an option, not the
        // shell: "-oProxyCommand=..." would run whatever it names on this Mac.
        if (_sshTarget.Length == 0 || _sshTarget.StartsWith('-') || _sshTarget.Any(char.IsWhiteSpace))
            throw new RelayConnectionException("Enter the relay's server as user@host, for example admin@relay-host.");
        if (_configPath.Length == 0)
            throw new RelayConnectionException("Enter the path to the relay's appsettings.json on that server.");
    }

    public string SshTarget => _sshTarget;

    public bool CanConfirm => _serviceName.Length > 0;

    // For "Test connection": proves ssh works, the file is the relay's, and whether the relay
    // itself is running - without changing anything.
    public async Task<string> DescribeAsync(CancellationToken cancellationToken)
    {
        var blocked = await ReadAsync(cancellationToken);
        var summary = $"Connected to {_sshTarget}. {_configPath} is the relay's settings file, with {blocked.Count} blocked license(s).";
        if (!CanConfirm) return summary + " No service name is set, so changes won't be confirmed.";

        var state = (await RunAsync($"systemctl is-active {_serviceName} || true", null, cancellationToken)).Trim();
        return summary + (state == "active"
            ? $" The {_serviceName} service is running."
            : $" The {_serviceName} service is {(state.Length > 0 ? state : "not found")} - blocking takes effect only once it runs.");
    }

    public async Task<IReadOnlyList<string>> ReadAsync(CancellationToken cancellationToken)
    {
        var relay = RelaySection(await ReadConfigAsync(cancellationToken));
        return BlockedIds(relay);
    }

    // Read, change, write as one step against a fresh copy of the file, so an edit made on the box
    // since this window last looked isn't overwritten with a stale version.
    public async Task<IReadOnlyList<string>> UpdateAsync(Func<List<string>, List<string>> change, CancellationToken cancellationToken)
    {
        var root = await ReadConfigAsync(cancellationToken);
        var relay = RelaySection(root);
        var updated = change(BlockedIds(relay).ToList());
        relay["BlockedLicenseIds"] = new JsonArray(updated.Select(id => (JsonNode?)JsonValue.Create(id)).ToArray());

        // Written beside the real file and renamed over it: the relay reloads the moment the file
        // changes, and a half-written file read at that moment would be invalid JSON.
        var path = RemotePath(_configPath);
        await RunAsync(
            $"f={path}; t=\"$f.sentinel-licenses.$$\"; cat > \"$t\" && mv -f -- \"$t\" \"$f\"",
            root.ToJsonString(WriteOptions) + "\n",
            cancellationToken);
        return updated;
    }

    // The relay logs "N license(s) blocked" every time it reloads the file (see its Program.cs).
    // Seeing the new count there is the only real proof the change took; a relay built before
    // blocking existed saves the file happily and ignores it.
    public async Task<bool> WaitForRelayToConfirmAsync(int expectedCount, CancellationToken cancellationToken)
    {
        if (!CanConfirm) return false;
        for (var attempt = 0; attempt < 6; attempt++)
        {
            await Task.Delay(TimeSpan.FromSeconds(1), cancellationToken);
            var output = await RunAsync(
                $"journalctl -u {_serviceName} --since -60s -o cat --no-pager | grep 'license(s) blocked' | tail -n 1 || true",
                null,
                cancellationToken);
            var match = BlockedCountPattern().Match(output);
            if (match.Success && int.Parse(match.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture) == expectedCount)
                return true;
        }
        return false;
    }

    private async Task<JsonObject> ReadConfigAsync(CancellationToken cancellationToken)
    {
        var text = await RunAsync($"cat -- {RemotePath(_configPath)}", null, cancellationToken);
        try
        {
            return JsonNode.Parse(text) as JsonObject
                ?? throw new RelayConnectionException($"{_configPath} on {_sshTarget} isn't a JSON object.");
        }
        catch (JsonException ex)
        {
            // Never rewrite a file this couldn't read: it would replace it with a guess.
            throw new RelayConnectionException($"{_configPath} on {_sshTarget} isn't valid JSON ({ex.Message}). Fix it by hand first.");
        }
    }

    private JsonObject RelaySection(JsonObject root) =>
        root["Relay"] as JsonObject
        ?? throw new RelayConnectionException($"{_configPath} on {_sshTarget} has no \"Relay\" section - is that the push relay's appsettings.json?");

    private static List<string> BlockedIds(JsonObject relay) =>
        relay["BlockedLicenseIds"] is JsonArray array
            ? array.Select(node => node is JsonValue value && value.TryGetValue<string>(out var id) ? id : node?.ToJsonString() ?? "")
                .Where(id => id.Length > 0)
                .ToList()
            : [];

    // "~/..." has to be expanded by the remote shell, so it becomes "$HOME"/'...'; everything else is
    // single-quoted so a space or quote in the path can't change the command.
    private static string RemotePath(string path) =>
        path.StartsWith("~/", StringComparison.Ordinal) ? "\"$HOME\"/" + ShellQuote(path[2..]) : ShellQuote(path);

    private static string ShellQuote(string value) => "'" + value.Replace("'", "'\\''", StringComparison.Ordinal) + "'";

    private async Task<string> RunAsync(string remoteCommand, string? input, CancellationToken cancellationToken)
    {
        var start = new ProcessStartInfo("ssh")
        {
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        };
        foreach (var argument in new[] { "-o", "BatchMode=yes", "-o", "ConnectTimeout=10", _sshTarget, remoteCommand })
            start.ArgumentList.Add(argument);

        using var process = new Process { StartInfo = start };
        try
        {
            process.Start();
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            throw new RelayConnectionException($"Couldn't run ssh on this computer: {ex.Message}");
        }

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(CommandTimeout);
        var stdout = process.StandardOutput.ReadToEndAsync(timeout.Token);
        var stderr = process.StandardError.ReadToEndAsync(timeout.Token);
        try
        {
            if (input is not null) await process.StandardInput.WriteAsync(new StringBuilder(input), timeout.Token);
            process.StandardInput.Close();
            await process.WaitForExitAsync(timeout.Token);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            try { process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
            throw new RelayConnectionException($"{_sshTarget} didn't answer within {CommandTimeout.TotalSeconds:0} seconds.");
        }

        var output = await stdout;
        var error = (await stderr).Trim();
        return process.ExitCode switch
        {
            0 => output,
            // 255 is ssh's own "couldn't connect / authenticate", as opposed to the remote command failing.
            255 => throw new RelayConnectionException(
                $"Couldn't connect to {_sshTarget}: {(error.Length > 0 ? error : "ssh gave no reason")}. " +
                "This uses your own ssh keys - check that `ssh " + _sshTarget + "` works in Terminal without a password."),
            _ => throw new RelayConnectionException($"The command on {_sshTarget} failed: {(error.Length > 0 ? error : $"exit code {process.ExitCode}")}")
        };
    }

    [GeneratedRegex(@"(\d+) license\(s\) blocked")]
    private static partial Regex BlockedCountPattern();

    // Goes into a remote command unquoted, so it's held to what a systemd unit name can be.
    [GeneratedRegex(@"^[A-Za-z0-9._@-]+$")]
    private static partial Regex ServiceNamePattern();
}
