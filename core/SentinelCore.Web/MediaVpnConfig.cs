using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Org.BouncyCastle.Crypto.Parameters;
using Org.BouncyCastle.Security;

namespace SentinelCore.Web;

public static class MediaVpnProviders
{
    // The file install-downloads.sh put in /etc/sentinelcore/vpn (WireGuard or OpenVPN).
    public const string Installed = "installed";
    // A Mullvad account number: this server makes the WireGuard key and registers it.
    public const string Mullvad = "mullvad";
    // Any other WireGuard provider, its values typed into Settings.
    public const string WireGuard = "wireguard";

    public static bool IsValid(string? provider) => provider is Installed or Mullvad or WireGuard;
}

// Turns Settings -> MOVIE DOWNLOADS -> VPN Provider into the WireGuard configuration the tunnel
// starts with. Only ever WireGuard: sentinelcore-vpn runs as root and reads what this writes,
// and a WireGuard file - checked line by line on that side too - can only describe keys,
// addresses and a server, never a command to run. OpenVPN files (which can) stay install-time
// only, through install-downloads.sh.
public sealed partial class MediaVpnConfig(IHttpClientFactory httpClientFactory, string dataDirectory, ILogger<MediaVpnConfig> logger)
{
    public const string HttpClientName = "Mullvad";
    private const string MullvadApi = "https://api.mullvad.net";
    private const string MullvadDns = "10.64.0.1";

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    // What this server registered with Mullvad, so a restart reuses the same device and server
    // rather than filling the account's five device slots.
    public sealed class MullvadState
    {
        public string AccountHash { get; set; } = "";
        public string DeviceId { get; set; } = "";
        public string DeviceName { get; set; } = "";
        public string PrivateKey { get; set; } = "";
        public string Ipv4 { get; set; } = "";
        public string? Ipv6 { get; set; }
        public string Location { get; set; } = "";
        public string RelayHostname { get; set; } = "";
        public string RelayIp { get; set; } = "";
        public string RelayPublicKey { get; set; } = "";
    }

    private string StatePath => Path.Combine(dataDirectory, "media-downloads-mullvad.json");

    // The WireGuard file for the chosen provider, or null for "the installed one".
    public async Task<string?> BuildAsync(MediaDownloadsSettingsDto options, CancellationToken cancellationToken)
    {
        switch (options.VpnProvider)
        {
            case MediaVpnProviders.WireGuard:
            {
                var errors = Validate(options);
                if (errors.Count > 0) throw new MediaDownloadException(string.Join(" ", errors));
                return Render(options.WireGuardPrivateKey.Trim(), SplitList(options.WireGuardAddress), SplitList(options.WireGuardDns),
                    options.WireGuardServerPublicKey.Trim(), options.WireGuardServerEndpoint.Trim());
            }
            case MediaVpnProviders.Mullvad:
            {
                var state = await EnsureMullvadAsync(options, cancellationToken);
                return Render(state.PrivateKey, new[] { state.Ipv4, state.Ipv6 }.OfType<string>().Where(a => a.Length > 0).ToList(), [MullvadDns],
                    state.RelayPublicKey, $"{state.RelayIp}:51820");
            }
            default:
                return null;
        }
    }

    public static string Render(string privateKey, IReadOnlyList<string> addresses, IReadOnlyList<string> dns, string serverPublicKey, string endpoint)
    {
        var text = new StringBuilder();
        text.Append("[Interface]\n");
        text.Append($"PrivateKey = {privateKey}\n");
        text.Append($"Address = {string.Join(",", addresses)}\n");
        if (dns.Count > 0) text.Append($"DNS = {string.Join(",", dns)}\n");
        text.Append("\n[Peer]\n");
        text.Append($"PublicKey = {serverPublicKey}\n");
        text.Append("AllowedIPs = 0.0.0.0/0,::/0\n");
        text.Append($"Endpoint = {endpoint}\n");
        return text.ToString();
    }

    // What Settings refuses to save, worded for the form.
    public static List<string> Validate(MediaDownloadsSettingsDto options)
    {
        var errors = new List<string>();
        if (!MediaVpnProviders.IsValid(options.VpnProvider)) errors.Add("VPN Provider must be installed, mullvad or wireguard.");
        if (options.VpnProvider == MediaVpnProviders.Mullvad)
        {
            if (NormalizeAccount(options.MullvadAccountNumber) is null) errors.Add("The Mullvad account number is the 16 digits on mullvad.net (spaces are fine).");
            if (!LocationPattern().IsMatch(options.MullvadLocation?.Trim() ?? "")) errors.Add("Mullvad Location is a country code like se, or a city like se-sto.");
        }
        if (options.VpnProvider == MediaVpnProviders.WireGuard)
        {
            if (!IsKey(options.WireGuardPrivateKey)) errors.Add("WireGuard Private Key must be the 44-character key from your provider's WireGuard file (PrivateKey = ...).");
            var addresses = SplitList(options.WireGuardAddress);
            if (addresses.Count == 0 || !addresses.All(IsCidr)) errors.Add("WireGuard Address must be the Address line from the file, like 10.2.0.2/32 (several separated by commas).");
            if (!SplitList(options.WireGuardDns).All(a => IPAddress.TryParse(a, out _))) errors.Add("WireGuard DNS must be IP addresses, like 10.2.0.1.");
            if (!IsKey(options.WireGuardServerPublicKey)) errors.Add("Server Public Key must be the 44-character key from the file's [Peer] section (PublicKey = ...).");
            if (!EndpointPattern().IsMatch(options.WireGuardServerEndpoint?.Trim() ?? "")) errors.Add("Server Endpoint must be the Endpoint line, like 185.195.233.76:51820 or se-sto.example.net:51820.");
        }
        return errors;
    }

    // MARK: - Mullvad

    private async Task<MullvadState> EnsureMullvadAsync(MediaDownloadsSettingsDto options, CancellationToken cancellationToken)
    {
        var account = NormalizeAccount(options.MullvadAccountNumber)
            ?? throw new MediaDownloadException("Enter your Mullvad account number under Settings -> MOVIE DOWNLOADS.");
        var location = options.MullvadLocation.Trim().ToLowerInvariant();
        var state = ReadState();
        var accountHash = AccountHash(account);
        var changed = false;

        try
        {
            if (state is null || state.AccountHash != accountHash)
            {
                state = await RegisterDeviceAsync(account, accountHash, cancellationToken);
                changed = true;
            }
            if (state.Location != location || state.RelayHostname.Length == 0)
            {
                await PickRelayAsync(state, location, cancellationToken);
                changed = true;
            }
        }
        catch (HttpRequestException ex)
        {
            throw new MediaDownloadException($"Could not reach Mullvad to set up the VPN: {ex.Message}");
        }
        if (changed) WriteState(state);
        return state;
    }

    // Called when the tunnel will not come up with the saved device: it may have been removed on
    // mullvad.net. Checked rather than assumed, so a Mullvad outage never costs a device slot.
    public async Task<bool> ForgetMullvadDeviceIfGoneAsync(MediaDownloadsSettingsDto options, CancellationToken cancellationToken)
    {
        var state = ReadState();
        var account = NormalizeAccount(options.MullvadAccountNumber);
        if (state is null || account is null) return false;
        try
        {
            var token = await TokenAsync(account, cancellationToken);
            using var request = new HttpRequestMessage(HttpMethod.Get, $"{MullvadApi}/accounts/v1/devices/{state.DeviceId}");
            request.Headers.Authorization = new("Bearer", token);
            using var response = await Client().SendAsync(request, cancellationToken);
            if (response.StatusCode != HttpStatusCode.NotFound) return false;
            logger.LogWarning("Movie downloads: Mullvad device {Name} was removed from the account; registering a new one", state.DeviceName);
            File.Delete(StatePath);
            return true;
        }
        catch (Exception ex) when (ex is HttpRequestException or MediaDownloadException or IOException) { return false; }
    }

    // When the account's paid time runs out ("expiry" on accounts/v1/accounts/me). Signing in still
    // works on an account that has run out - that is how Mullvad's own app shows the date - so
    // this answers either way. Throws MediaDownloadException for a wrong number,
    // HttpRequestException when Mullvad cannot be reached.
    public async Task<DateTimeOffset> MullvadPaidUntilAsync(string account, CancellationToken cancellationToken)
    {
        var token = await TokenAsync(account, cancellationToken);
        using var request = new HttpRequestMessage(HttpMethod.Get, $"{MullvadApi}/accounts/v1/accounts/me");
        request.Headers.Authorization = new("Bearer", token);
        using var response = await Client().SendAsync(request, cancellationToken);
        response.EnsureSuccessStatusCode();
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
        return DateTimeOffset.TryParse(Str(document.RootElement, "expiry"), System.Globalization.CultureInfo.InvariantCulture,
            System.Globalization.DateTimeStyles.AssumeUniversal, out var expiry)
            ? expiry
            : throw new MediaDownloadException("Mullvad answered without the account's paid-until date.");
    }

    public static string AccountHash(string account) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(account)));

    private async Task<MullvadState> RegisterDeviceAsync(string account, string accountHash, CancellationToken cancellationToken)
    {
        var token = await TokenAsync(account, cancellationToken);
        var privateKey = new X25519PrivateKeyParameters(new SecureRandom());
        var publicKey = Convert.ToBase64String(privateKey.GeneratePublicKey().GetEncoded());
        using var request = new HttpRequestMessage(HttpMethod.Post, $"{MullvadApi}/accounts/v1/devices")
        {
            Content = JsonContent.Create(new { pubkey = publicKey, hijack_dns = false })
        };
        request.Headers.Authorization = new("Bearer", token);
        using var response = await Client().SendAsync(request, cancellationToken);
        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            if (body.Contains("MAX_DEVICES", StringComparison.OrdinalIgnoreCase))
                throw new MediaDownloadException("Your Mullvad account already has its maximum of 5 devices. Remove one at mullvad.net (Account -> Devices), then try again.");
            throw new MediaDownloadException($"Mullvad would not register a key for this server ({(int)response.StatusCode}).");
        }
        using var document = JsonDocument.Parse(body);
        var root = document.RootElement;
        var state = new MullvadState
        {
            AccountHash = accountHash,
            DeviceId = Str(root, "id") ?? "",
            DeviceName = Str(root, "name") ?? "",
            PrivateKey = Convert.ToBase64String(privateKey.GetEncoded()),
            Ipv4 = Str(root, "ipv4_address") ?? "",
            Ipv6 = Str(root, "ipv6_address")
        };
        if (state.DeviceId.Length == 0 || state.Ipv4.Length == 0) throw new MediaDownloadException("Mullvad answered without an address for this server.");
        logger.LogInformation("Movie downloads: registered this server with Mullvad as device \"{Name}\"", state.DeviceName);
        return state;
    }

    private async Task<string> TokenAsync(string account, CancellationToken cancellationToken)
    {
        using var response = await Client().PostAsJsonAsync($"{MullvadApi}/auth/v1/token", new { account_number = account }, cancellationToken);
        if (response.StatusCode is HttpStatusCode.BadRequest or HttpStatusCode.Unauthorized or HttpStatusCode.NotFound)
            throw new MediaDownloadException("Mullvad did not recognise that account number. Check it under Settings -> MOVIE DOWNLOADS.");
        response.EnsureSuccessStatusCode();
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
        return Str(document.RootElement, "access_token") ?? throw new MediaDownloadException("Mullvad did not sign this server in.");
    }

    private async Task PickRelayAsync(MullvadState state, string location, CancellationToken cancellationToken)
    {
        var relays = await RelaysAsync(cancellationToken);
        var matching = relays.Relays
            .Where(r => r.Active && (r.Location == location || r.Location.StartsWith(location + "-", StringComparison.Ordinal)))
            .ToList();
        if (matching.Count == 0) throw new MediaDownloadException($"Mullvad has no WireGuard servers at \"{location}\" right now. Pick another location.");
        // Mullvad's own servers first (not rented), then any one of them, so the household does
        // not all land on the same machine.
        var pool = matching.Any(r => r.Owned) ? matching.Where(r => r.Owned).ToList() : matching;
        var relay = pool[Random.Shared.Next(pool.Count)];
        state.Location = location;
        state.RelayHostname = relay.Hostname;
        state.RelayIp = relay.Ipv4;
        state.RelayPublicKey = relay.PublicKey;
    }

    public sealed record Relay(string Hostname, string Location, bool Active, bool Owned, string Ipv4, string PublicKey);

    public sealed record RelayList(List<Relay> Relays, Dictionary<string, (string Country, string City)> Locations);

    public async Task<RelayList> RelaysAsync(CancellationToken cancellationToken)
    {
        using var document = JsonDocument.Parse(await Client().GetStringAsync($"{MullvadApi}/public/relays/wireguard/v2", cancellationToken));
        var root = document.RootElement;
        var relays = new List<Relay>();
        if (root.TryGetProperty("wireguard", out var wireguard) && wireguard.TryGetProperty("relays", out var list))
            foreach (var r in list.EnumerateArray())
            {
                if (Str(r, "hostname") is not { } hostname || Str(r, "ipv4_addr_in") is not { } ip || Str(r, "public_key") is not { } key) continue;
                relays.Add(new Relay(hostname, Str(r, "location") ?? "", Bool(r, "active"), Bool(r, "owned"), ip, key));
            }
        var locations = new Dictionary<string, (string, string)>();
        if (root.TryGetProperty("locations", out var locs) && locs.ValueKind == JsonValueKind.Object)
            foreach (var loc in locs.EnumerateObject())
                locations[loc.Name] = (Str(loc.Value, "country") ?? loc.Name, Str(loc.Value, "city") ?? "");
        return new RelayList(relays, locations);
    }

    // The choices for the Mullvad Location box: every country ("Sweden - any city") and city
    // that has an active WireGuard server.
    public async Task<List<SettingsFieldOption>> MullvadLocationsAsync(CancellationToken cancellationToken)
    {
        var list = await RelaysAsync(cancellationToken);
        var cities = list.Relays.Where(r => r.Active).Select(r => r.Location).Distinct().ToList();
        var options = new List<SettingsFieldOption>();
        foreach (var country in cities.GroupBy(c => c.Split('-')[0]).OrderBy(g => CountryName(list, g.First())))
        {
            options.Add(new SettingsFieldOption(country.Key, $"{CountryName(list, country.First())} - any city"));
            foreach (var city in country.OrderBy(c => list.Locations.TryGetValue(c, out var l) ? l.City : c))
                options.Add(new SettingsFieldOption(city, $"{CountryName(list, city)} - {(list.Locations.TryGetValue(city, out var l) && l.City.Length > 0 ? l.City : city)}"));
        }
        return options;
    }

    private static string CountryName(RelayList list, string code) => list.Locations.TryGetValue(code, out var l) ? l.Country : code;

    public MullvadState? ReadState()
    {
        try { return File.Exists(StatePath) ? JsonSerializer.Deserialize<MullvadState>(File.ReadAllText(StatePath), JsonOptions) : null; }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException) { return null; }
    }

    private void WriteState(MullvadState state)
    {
        Directory.CreateDirectory(dataDirectory);
        var temporary = StatePath + ".tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(state, JsonOptions));
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(temporary, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        File.Move(temporary, StatePath, overwrite: true);
    }

    private HttpClient Client() => httpClientFactory.CreateClient(HttpClientName);

    // MARK: - Parsing

    public static string? NormalizeAccount(string? account)
    {
        var digits = new string((account ?? "").Where(char.IsDigit).ToArray());
        return digits.Length == 16 && (account ?? "").All(c => char.IsDigit(c) || c is ' ' or '-') ? digits : null;
    }

    public static List<string> SplitList(string? text) =>
        (text ?? "").Split([',', ' ', '\n', '\r', '\t'], StringSplitOptions.RemoveEmptyEntries).ToList();

    private static bool IsKey(string? key)
    {
        var text = key?.Trim() ?? "";
        if (text.Length != 44) return false;
        try { return Convert.FromBase64String(text).Length == 32; }
        catch (FormatException) { return false; }
    }

    private static bool IsCidr(string text)
    {
        var parts = text.Split('/');
        return parts.Length == 2 && IPAddress.TryParse(parts[0], out var ip) && int.TryParse(parts[1], out var bits)
            && bits >= 0 && bits <= (ip.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6 ? 128 : 32);
    }

    [GeneratedRegex(@"^[a-z]{2}(-[a-z]{3})?$")]
    private static partial Regex LocationPattern();

    [GeneratedRegex(@"^([A-Za-z0-9.-]+|\[[0-9A-Fa-f:]+\]):[0-9]{1,5}$")]
    private static partial Regex EndpointPattern();

    private static string? Str(JsonElement e, string key) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static bool Bool(JsonElement e, string key) => e.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.True;
}
