using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace SentinelCore.Client.Networking;

/// <summary>
/// A SentinelCore server found on the local network via mDNS/DNS-SD. Purely a convenience for
/// prefilling the server setup screen's manual address field; nothing here is persisted directly.
/// </summary>
public sealed record DiscoveredServer(string Id, string Name, string Host, int Port, string Scheme)
{
    public string AddressString => $"{Scheme}://{Host}:{Port}";
}

/// <summary>
/// Browses for SentinelCore servers on the LAN so the setup screen can list them instead of the
/// user typing an IP address.
/// </summary>
/// <remarks>
/// "_sentinelcore._tcp" rather than "_http._tcp": a dedicated service type means every result this
/// browser ever sees is actually a SentinelCore server, with no further filtering needed. It is the
/// same type the server advertises (MdnsAdvertiserService.cs) and the same one the Android and
/// Apple clients browse for.
///
/// Purely an assist: discovery can legitimately find nothing - client/AP isolation, a different
/// VLAN, a firewall that drops multicast - and manual entry must keep working regardless.
/// </remarks>
public sealed class ServerDiscovery : IDisposable
{
    private const string ServiceType = "_sentinelcore._tcp.local";
    private static readonly IPAddress MulticastAddress = IPAddress.Parse("224.0.0.251");
    private const int MulticastPort = 5353;

    // Re-asked periodically rather than once: a server that boots after this app did would
    // otherwise never appear, and the setup screen is exactly where someone is likely to be
    // powering the server on as they look at it.
    private static readonly TimeSpan QueryInterval = TimeSpan.FromSeconds(5);

    private readonly ISentinelLog _log;
    private readonly object _gate = new();
    private readonly Dictionary<string, DiscoveredServer> _servers = new(StringComparer.OrdinalIgnoreCase);
    private CancellationTokenSource? _cancellation;

    public ServerDiscovery(ISentinelLog log) => _log = log;

    /// <summary>Raised on a background thread whenever the set of known servers changes.</summary>
    public event Action<IReadOnlyList<DiscoveredServer>>? ServersChanged;

    public IReadOnlyList<DiscoveredServer> Servers
    {
        get
        {
            lock (_gate) return _servers.Values.OrderBy(server => server.Name).ToList();
        }
    }

    public void Start()
    {
        if (_cancellation is not null) return;
        _cancellation = new CancellationTokenSource();
        var token = _cancellation.Token;

        // One listener per interface. A PC with Wi-Fi and Ethernet both up (or a Hyper-V switch,
        // which is very common on a machine that also runs the server) needs the query to leave
        // every one of them - binding to Any sends it out whichever the routing table prefers,
        // which is regularly the virtual adapter nothing is listening on.
        foreach (var address in LocalIPv4Addresses())
        {
            _ = Task.Run(() => BrowseAsync(address, token), token);
        }
    }

    public void Stop()
    {
        var cancellation = _cancellation;
        _cancellation = null;
        cancellation?.Cancel();
        cancellation?.Dispose();
    }

    private async Task BrowseAsync(IPAddress localAddress, CancellationToken ct)
    {
        UdpClient? client = null;
        try
        {
            client = new UdpClient(AddressFamily.InterNetwork);
            client.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
            client.Client.Bind(new IPEndPoint(localAddress, 0));
            client.JoinMulticastGroup(MulticastAddress, localAddress);
            client.MulticastLoopback = true;

            var query = MdnsMessage.BuildQuery(ServiceType);
            var endpoint = new IPEndPoint(MulticastAddress, MulticastPort);

            var querying = Task.Run(async () =>
            {
                while (!ct.IsCancellationRequested)
                {
                    try
                    {
                        await client.SendAsync(query, query.Length, endpoint).ConfigureAwait(false);
                    }
                    catch (Exception error) when (error is SocketException or ObjectDisposedException)
                    {
                        return;
                    }
                    try
                    {
                        await Task.Delay(QueryInterval, ct).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException)
                    {
                        return;
                    }
                }
            }, ct);

            while (!ct.IsCancellationRequested)
            {
                var result = await client.ReceiveAsync(ct).ConfigureAwait(false);
                HandlePacket(result.Buffer);
            }

            await querying.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Stop() was called; nothing to report.
        }
        catch (Exception error) when (error is SocketException or ObjectDisposedException)
        {
            // One interface refusing to join the multicast group (a disconnected adapter, a
            // policy-blocked virtual switch) must not stop the others from finding anything.
            _log.Log($"Local-network discovery unavailable on {localAddress}: {error.Message}", DiagnosticLevel.Notice);
        }
        finally
        {
            client?.Dispose();
        }
    }

    private void HandlePacket(byte[] packet)
    {
        var records = MdnsMessage.ParseRecords(packet);
        if (records.Count == 0) return;

        // Everything a responder knows about a service arrives in one packet: the PTR naming the
        // instance, the SRV giving host and port, the TXT carrying the scheme, and the A record
        // for the host. Index them by name first, then join.
        var srvByName = new Dictionary<string, MdnsMessage.ServiceRecord>(StringComparer.OrdinalIgnoreCase);
        var txtByName = new Dictionary<string, Dictionary<string, string>>(StringComparer.OrdinalIgnoreCase);
        var addressByHost = new Dictionary<string, IPAddress>(StringComparer.OrdinalIgnoreCase);
        var instances = new List<IReadOnlyList<string>>();

        foreach (var record in records)
        {
            switch (record.Type)
            {
                case MdnsMessage.TypePtr when record.DisplayName.Equals(ServiceType, StringComparison.OrdinalIgnoreCase):
                    if (MdnsMessage.ReadPtrTarget(packet, record) is { Count: > 0 } target) instances.Add(target);
                    break;
                case MdnsMessage.TypeSrv:
                    if (MdnsMessage.ReadSrv(packet, record) is { } srv) srvByName[record.DisplayName] = srv;
                    break;
                case MdnsMessage.TypeTxt:
                    txtByName[record.DisplayName] = MdnsMessage.ReadTxt(record);
                    break;
                case MdnsMessage.TypeA:
                    if (MdnsMessage.ReadA(record) is { } address) addressByHost[record.DisplayName] = address;
                    break;
            }
        }

        // A responder answering a browse query sends the PTR; one answering a resolve sends only
        // SRV/TXT. Take instance names from both so a second packet completing an earlier one
        // still lands.
        foreach (var name in srvByName.Keys.Where(name =>
                     name.EndsWith(ServiceType, StringComparison.OrdinalIgnoreCase)))
        {
            if (instances.All(labels => !string.Join('.', labels).Equals(name, StringComparison.OrdinalIgnoreCase)))
            {
                instances.Add(name.Split('.'));
            }
        }

        var changed = false;
        foreach (var labels in instances)
        {
            var instanceName = string.Join('.', labels);
            if (!srvByName.TryGetValue(instanceName, out var srv)) continue;

            var host = addressByHost.TryGetValue(srv.TargetName, out var resolved)
                ? resolved.ToString()
                : srv.TargetName;
            if (string.IsNullOrWhiteSpace(host)) continue;

            var scheme = txtByName.TryGetValue(instanceName, out var txt) &&
                         txt.TryGetValue("scheme", out var value) &&
                         !string.IsNullOrWhiteSpace(value)
                ? value
                : "http";

            // The instance's own label is its display name; the rest is the service type.
            var displayName = labels.Count > 0 ? labels[0] : instanceName;
            var server = new DiscoveredServer(instanceName, displayName, host, srv.Port, scheme);

            lock (_gate)
            {
                if (_servers.TryGetValue(instanceName, out var existing) && existing == server) continue;
                _servers[instanceName] = server;
                changed = true;
            }
        }

        if (changed) ServersChanged?.Invoke(Servers);
    }

    private static IEnumerable<IPAddress> LocalIPv4Addresses()
    {
        foreach (var adapter in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (adapter.OperationalStatus != OperationalStatus.Up) continue;
            if (adapter.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;
            if (!adapter.SupportsMulticast) continue;

            foreach (var unicast in adapter.GetIPProperties().UnicastAddresses)
            {
                if (unicast.Address.AddressFamily == AddressFamily.InterNetwork)
                {
                    yield return unicast.Address;
                }
            }
        }
    }

    public void Dispose() => Stop();
}
