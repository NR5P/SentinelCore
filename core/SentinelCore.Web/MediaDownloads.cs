using System.ComponentModel.DataAnnotations;

namespace SentinelCore.Web;

// "+ Add" on the Movies tab: find a movie or TV episodes on the torrent sites the household has
// added, download it through a VPN, and file it into the library. The server never talks to a
// torrent site or peer itself - Prowlarr (sites and search) and qBittorrent (the downloading) do,
// both confined to a network namespace whose only way out is the VPN tunnel. On Linux,
// core/downloads/install-downloads.sh sets all of that up, and this server starts and stops it
// on demand (MediaDownloadStack). Contract: core/docs/MOVIE_DOWNLOADS.md.

public static class MediaDownloadStates
{
    // Waiting for the VPN and the torrent services to come up.
    public const string Queued = "queued";
    public const string Downloading = "downloading";
    // Finished downloading, sharing back for the minutes Settings asks for before it is filed.
    public const string Seeding = "seeding";
    public const string Importing = "importing";
    public const string Done = "done";
    public const string Failed = "failed";
    public const string Canceled = "canceled";

    public static bool IsActive(string state) => state is Queued or Downloading or Seeding or Importing;
}

public sealed class MediaDownload
{
    public Guid Id { get; set; }
    // movie or episode - what the request was for. A season pack is an episode request with no
    // episode number.
    public string Kind { get; set; } = MediaKinds.Movie;
    public string Title { get; set; } = string.Empty;
    public int? Year { get; set; }
    public int? TmdbId { get; set; }
    public int? Season { get; set; }
    public int? Episode { get; set; }
    public string? PosterPath { get; set; }

    public string ReleaseTitle { get; set; } = string.Empty;
    public string? Indexer { get; set; }
    public long? SizeBytes { get; set; }
    // What qBittorrent was given: a magnet link, or a URL the server fetched a .torrent from.
    public string Source { get; set; } = string.Empty;
    public string? InfoHash { get; set; }

    public string State { get; set; } = MediaDownloadStates.Queued;
    public double Progress { get; set; }
    public long DownloadedBytes { get; set; }
    public long SpeedBytesPerSecond { get; set; }
    public long? EtaSeconds { get; set; }
    public int Peers { get; set; }
    public string? Error { get; set; }

    public Guid LibraryId { get; set; }
    // Where qBittorrent writes (a hidden folder inside the library, so the move is a rename).
    public string StagingPath { get; set; } = string.Empty;
    public List<string> ImportedPaths { get; set; } = [];
    public Guid? ItemId { get; set; }
    // The copy already in the library that this download replaces: its files are deleted once
    // the new one is ready to be filed.
    public Guid? ReplaceItemId { get; set; }

    public string RequestedBy { get; set; } = string.Empty;
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? CompletedAt { get; set; }
    public DateTimeOffset? FinishedAt { get; set; }
}

// One torrent a site offered, as shown in the "pick a release" list.
public sealed record MediaRelease(
    string Id,
    string Title,
    string? Indexer,
    long? SizeBytes,
    int? Seeders,
    int? Leechers,
    DateTimeOffset? PublishedAt,
    string? Quality,
    string? Codec,
    string? Source,
    bool Hdr,
    int? Season,
    int? Episode,
    bool SeasonPack,
    bool Matches,
    string? Warning,
    double Score,
    // Never sent to clients: the magnet or .torrent URL the grab uses.
    string DownloadSource,
    string? InfoHash);

public sealed class MediaDownloadsSettingsDto
{
    public const string SectionName = "MediaDownloads";

    public const string QualityAny = "any";

    public bool Enabled { get; set; }
    // On: this server starts and stops the VPN, qBittorrent and Prowlarr that
    // install-downloads.sh installed as systemd services. Off: they are someone else's to run.
    public bool ManageServices { get; set; } = true;
    // On: everything the sites and peers see goes through the VPN, proven before any search or
    // download. Off: the same services reach the internet over the home connection instead.
    public bool UseVpn { get; set; } = true;
    // Where the tunnel's WireGuard settings come from (MediaVpnConfig): the file
    // install-downloads.sh installed, a Mullvad account (keys made and registered by this
    // server), or any other WireGuard provider's values typed in.
    public string VpnProvider { get; set; } = MediaVpnProviders.Installed;
    public string MullvadAccountNumber { get; set; } = string.Empty;
    // A Mullvad location code: a country ("se") or a city ("se-sto").
    public string MullvadLocation { get; set; } = "se";
    public string WireGuardPrivateKey { get; set; } = string.Empty;
    public string WireGuardAddress { get; set; } = string.Empty;
    public string WireGuardDns { get; set; } = string.Empty;
    public string WireGuardServerPublicKey { get; set; } = string.Empty;
    public string WireGuardServerEndpoint { get; set; } = string.Empty;
    public string AllowedUsers { get; set; } = "admins";
    public string ProwlarrUrl { get; set; } = "http://10.250.0.2:9696";
    public string ProwlarrApiKey { get; set; } = string.Empty;
    public string QbittorrentUrl { get; set; } = "http://10.250.0.2:8080";
    public string QbittorrentUsername { get; set; } = string.Empty;
    public string QbittorrentPassword { get; set; } = string.Empty;
    public string PreferredQuality { get; set; } = "1080p";
    [Range(0, 10080)]
    public int SeedMinutes { get; set; }
    [Range(1, 240)]
    public int IdleMinutes { get; set; } = 5;
}

public sealed class MediaDownloadSearchInput
{
    // movie or show.
    public string Kind { get; set; } = MediaKinds.Movie;
    public string Title { get; set; } = string.Empty;
    public int? Year { get; set; }
    public int? TmdbId { get; set; }
    public string? PosterPath { get; set; }
    // Shows only: a season (every episode of it), or one episode of it.
    public int? Season { get; set; }
    public int? Episode { get; set; }
}

public sealed class MediaDownloadGrabInput
{
    public Guid SearchId { get; set; }
    public string ReleaseId { get; set; } = string.Empty;
    public Guid? LibraryId { get; set; }
    // A movie already in the library: null asks (409 alreadyInLibrary), true replaces the old
    // copy, false keeps both.
    public bool? Replace { get; set; }
}

public sealed class MediaDownloadSiteInput
{
    public string Definition { get; set; } = string.Empty;
    public string? Name { get; set; }
    public Dictionary<string, System.Text.Json.JsonElement> Fields { get; set; } = [];
}

public sealed class MediaDownloadSiteUpdateInput
{
    public bool Enabled { get; set; }
}
