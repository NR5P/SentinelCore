using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using Makaretu.Dns;

namespace SentinelCore.Web;

public sealed record MediaNetworkHost(string Name, string Host, string? Address);

// Turns the name a person typed ("DESKTOP-4F2", "nas.local", "192.168.1.20") into an address,
// and finds computers sharing files on the network.
//
// Resolution: an IP is used as is; otherwise ordinary DNS, then multicast DNS for "name.local".
// That second step matters on Linux, where a plain Windows computer name does not resolve
// without extra packages - but Windows 10 and later, macOS and most NASes all answer mDNS.
//
// Discovery lists computers that advertise "_smb._tcp" over mDNS: Macs with File Sharing on,
// NASes, Linux boxes running Samba with Avahi. Windows does not advertise its shares that way,
// so a Windows computer is added by name or address instead (the apps say so).
public sealed class MediaNetworkResolver(ILogger<MediaNetworkResolver> logger)
{
    private static readonly TimeSpan MdnsWait = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan CacheLifetime = TimeSpan.FromMinutes(5);
    private readonly ConcurrentDictionary<string, (IPAddress Address, DateTime At)> _cache = new(StringComparer.OrdinalIgnoreCase);

    public IPAddress Resolve(string host)
    {
        if (IPAddress.TryParse(host, out var literal)) return literal;
        if (_cache.TryGetValue(host, out var cached) && DateTime.UtcNow - cached.At < CacheLifetime) return cached.Address;

        var address = TryDns(host);
        var local = host.EndsWith(".local", StringComparison.OrdinalIgnoreCase) ? host : host.Contains('.') ? null : host + ".local";
        if (address is null && local is not null) address = TryMdns(local);
        if (address is null && local is not null && !string.Equals(local, host, StringComparison.OrdinalIgnoreCase)) address = TryDns(local);
        if (address is null)
            throw new MediaNetworkException($"Could not find a computer called {host} on the network. Try its IP address instead (for example 192.168.1.20).");
        _cache[host] = (address, DateTime.UtcNow);
        return address;
    }

    private static IPAddress? TryDns(string host)
    {
        try
        {
            var addresses = Dns.GetHostAddresses(host);
            return addresses.FirstOrDefault(a => a.AddressFamily == AddressFamily.InterNetwork) ?? addresses.FirstOrDefault();
        }
        catch (SocketException) { return null; }
        catch (ArgumentException) { return null; }
    }

    private IPAddress? TryMdns(string name)
    {
        try
        {
            using var mdns = new MulticastService { UseIpv6 = false };
            IPAddress? found = null;
            using var done = new ManualResetEventSlim();
            mdns.AnswerReceived += (_, e) =>
            {
                foreach (var record in e.Message.Answers.Concat(e.Message.AdditionalRecords).OfType<ARecord>())
                    if (string.Equals(record.Name.ToString().TrimEnd('.'), name, StringComparison.OrdinalIgnoreCase))
                    {
                        found = record.Address;
                        done.Set();
                    }
            };
            mdns.Start();
            mdns.SendQuery(new DomainName(name), DnsClass.IN, DnsType.A);
            done.Wait(MdnsWait);
            return found;
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "mDNS lookup of {Name} failed", name);
            return null;
        }
    }

    // Asks the network who shares files and listens for a few seconds.
    public async Task<IReadOnlyList<MediaNetworkHost>> DiscoverAsync(TimeSpan listen, CancellationToken cancellationToken)
    {
        var instances = new ConcurrentDictionary<string, byte>(StringComparer.OrdinalIgnoreCase);
        var targets = new ConcurrentDictionary<string, string>(StringComparer.OrdinalIgnoreCase);   // instance -> host name
        var addresses = new ConcurrentDictionary<string, IPAddress>(StringComparer.OrdinalIgnoreCase); // host name -> address
        const string service = "_smb._tcp.local";
        try
        {
            using var mdns = new MulticastService { UseIpv6 = false };
            mdns.AnswerReceived += (_, e) =>
            {
                foreach (var record in e.Message.Answers.Concat(e.Message.AdditionalRecords))
                {
                    switch (record)
                    {
                        case PTRRecord ptr when string.Equals(ptr.Name.ToString().TrimEnd('.'), service, StringComparison.OrdinalIgnoreCase):
                            instances.TryAdd(ptr.DomainName.ToString().TrimEnd('.'), 0);
                            break;
                        case SRVRecord srv:
                            targets[srv.Name.ToString().TrimEnd('.')] = srv.Target.ToString().TrimEnd('.');
                            break;
                        case ARecord a:
                            addresses[a.Name.ToString().TrimEnd('.')] = a.Address;
                            break;
                    }
                }
            };
            mdns.Start();
            mdns.SendQuery(new DomainName(service), DnsClass.IN, DnsType.PTR);
            var deadline = DateTime.UtcNow + listen;
            var asked = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            while (DateTime.UtcNow < deadline)
            {
                await Task.Delay(250, cancellationToken);
                // Responders that did not volunteer the SRV or address records get asked for them.
                foreach (var instance in instances.Keys.Where(i => !targets.ContainsKey(i) && asked.Add("srv:" + i)))
                    mdns.SendQuery(new DomainName(instance), DnsClass.IN, DnsType.SRV);
                foreach (var target in targets.Values.Where(t => !addresses.ContainsKey(t) && asked.Add("a:" + t)))
                    mdns.SendQuery(new DomainName(target), DnsClass.IN, DnsType.A);
            }
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Looking for file-sharing computers over mDNS failed");
        }

        var hosts = new List<MediaNetworkHost>();
        foreach (var instance in instances.Keys)
        {
            var label = instance.EndsWith("." + service, StringComparison.OrdinalIgnoreCase) ? instance[..^(service.Length + 1)] : instance;
            label = label.Replace("\\032", " ", StringComparison.Ordinal).Replace("\\", "", StringComparison.Ordinal);
            var target = targets.TryGetValue(instance, out var t) ? t : null;
            var address = target is not null && addresses.TryGetValue(target, out var a) ? a.ToString() : null;
            // "name.local" is what the person would type and what survives a DHCP change.
            var host = target ?? address;
            if (host is null) continue;
            if (hosts.Any(h => string.Equals(h.Host, host, StringComparison.OrdinalIgnoreCase))) continue;
            hosts.Add(new MediaNetworkHost(label, host, address));
            if (address is not null && IPAddress.TryParse(address, out var parsed)) _cache[host] = (parsed, DateTime.UtcNow);
        }
        return hosts.OrderBy(h => h.Name, StringComparer.OrdinalIgnoreCase).ToList();
    }
}
