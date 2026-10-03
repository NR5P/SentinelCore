using System.Net.Http;
using SentinelCore.Client.Models;

namespace SentinelCore.Client.Networking;

/// <summary>
/// The admin half of Movies &amp; TV: the libraries themselves, the folder picker that chooses
/// their folders, and the saved sign-ins for shared folders on other computers. The contract is
/// core/docs/MOVIES.md ("Libraries", "Folder picker", "Folders on other computers").
/// </summary>
/// <remarks>
/// Everything here but <see cref="MediaLibrariesAsync"/> is admin-only on the server. While Movies
/// &amp; TV is switched off every route answers 404 with "Movies &amp; TV is turned off in
/// Settings.", which arrives as a <see cref="SentinelServerException"/> carrying that message.
/// </remarks>
public sealed partial class SentinelClient
{
    /// <summary>The Movies &amp; TV routes - the default for the picker and network sign-ins below.</summary>
    public const string MediaBase = "/api/media";

    /// <summary>
    /// Music &amp; Podcasts serve the same picker and network routes under /api/audio (MUSIC_AND_PODCASTS.md §1),
    /// over the same saved sign-ins. Passing this as basePath points the methods below there.
    /// </summary>
    public const string AudioBase = "/api/audio";

    // MARK: - Libraries

    public Task<List<MediaLibrary>> MediaLibrariesAsync(CancellationToken ct = default) =>
        GetAsync<List<MediaLibrary>>($"{MediaBase}/libraries", ct: ct);

    /// <summary>Adding a library starts a scan of it. 400 (with every problem) when the folder can't be read.</summary>
    public Task<MediaLibrary> CreateMediaLibraryAsync(MediaLibraryInput input, CancellationToken ct = default) =>
        SendAsync<MediaLibrary, MediaLibraryInput>($"{MediaBase}/libraries", HttpMethod.Post, input, ct);

    /// <summary>Changing the path rescans.</summary>
    public Task<MediaLibrary> UpdateMediaLibraryAsync(string id, MediaLibraryInput input, CancellationToken ct = default) =>
        SendAsync<MediaLibrary, MediaLibraryInput>(
            $"{MediaBase}/libraries/{Uri.EscapeDataString(id)}", HttpMethod.Put, input, ct);

    /// <summary>Removes the library's titles and everyone's progress on them. The files are not touched.</summary>
    public Task DeleteMediaLibraryAsync(string id, CancellationToken ct = default) =>
        SendNoResultAsync<object>($"{MediaBase}/libraries/{Uri.EscapeDataString(id)}", HttpMethod.Delete, null, ct);

    /// <summary>202 when the scan starts; a 409 (already scanning) throws with the server's message.</summary>
    public Task ScanMediaLibraryAsync(string id, CancellationToken ct = default) =>
        SendNoResultAsync<object>($"{MediaBase}/libraries/{Uri.EscapeDataString(id)}/scan", HttpMethod.Post, null, ct);

    public Task ScanAllMediaLibrariesAsync(CancellationToken ct = default) =>
        SendNoResultAsync<object>($"{MediaBase}/scan", HttpMethod.Post, null, ct);

    // MARK: - Folder picker

    /// <summary>
    /// One page of the folder picker. Null or blank is the starting page (the server's own places
    /// plus the saved network computers). Never throws for a folder that can't be opened - that
    /// comes back as <see cref="MediaBrowseListing.Error"/>.
    /// </summary>
    public Task<MediaBrowseListing> MediaBrowseAsync(string? path = null, CancellationToken ct = default, string basePath = MediaBase)
    {
        var query = string.IsNullOrWhiteSpace(path)
            ? null
            : new[] { new KeyValuePair<string, string>("path", path.Trim()) };
        return GetAsync<MediaBrowseListing>($"{basePath}/browse", query, ct);
    }

    // MARK: - Network computers

    public Task<List<MediaNetworkComputer>> MediaNetworkComputersAsync(CancellationToken ct = default, string basePath = MediaBase) =>
        GetAsync<List<MediaNetworkComputer>>($"{basePath}/network", ct: ct);

    /// <summary>
    /// Signs in to a computer and saves the sign-in only if it works: POST for a new one, PUT when
    /// <paramref name="id"/> names a saved one (a blank password then keeps the saved password).
    /// </summary>
    /// <remarks>
    /// A failure throws <see cref="SentinelServerException"/>; its
    /// <see cref="SentinelServerException.NeedsLogin"/> says whether other credentials would help.
    /// </remarks>
    public Task<MediaNetworkSignInResult> SaveMediaNetworkComputerAsync(
        string? id, MediaNetworkComputerInput input, CancellationToken ct = default, string basePath = MediaBase) =>
        string.IsNullOrEmpty(id)
            ? SendAsync<MediaNetworkSignInResult, MediaNetworkComputerInput>($"{basePath}/network", HttpMethod.Post, input, ct)
            : SendAsync<MediaNetworkSignInResult, MediaNetworkComputerInput>(
                $"{basePath}/network/{Uri.EscapeDataString(id)}", HttpMethod.Put, input, ct);

    /// <summary>Libraries on that computer fail to scan until it is signed in to again.</summary>
    public Task DeleteMediaNetworkComputerAsync(string id, CancellationToken ct = default, string basePath = MediaBase) =>
        SendNoResultAsync<object>($"{basePath}/network/{Uri.EscapeDataString(id)}", HttpMethod.Delete, null, ct);

    /// <summary>
    /// Computers advertising file sharing over mDNS. Takes about three seconds on the server.
    /// Windows PCs never appear; they are added by name or address.
    /// </summary>
    public Task<List<MediaDiscoveredComputer>> DiscoverMediaComputersAsync(CancellationToken ct = default, string basePath = MediaBase) =>
        GetAsync<List<MediaDiscoveredComputer>>($"{basePath}/network/discover", ct: ct);
}
