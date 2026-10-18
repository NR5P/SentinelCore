using System.Net.Http;
using SentinelCore.Client.Models;

namespace SentinelCore.Client.Networking;

/// <summary>
/// "Suggested for you" on Music (core/docs/MUSIC_AND_PODCASTS.md "### Suggestions"). The server
/// builds the list in the background and waits up to 2 s for a build it starts, so a first read
/// often answers state working: ask again every few seconds until it is ready. Downloading is an
/// imported list: <see cref="DownloadMusicSuggestionsAsync"/>.
/// </summary>
public sealed partial class SentinelClient
{
    private const string SuggestionsPath = "/api/music/downloads/suggestions";

    /// <summary>The suggestions; <paramref name="refresh"/> starts a fresh build.</summary>
    public Task<MusicSuggestions> MusicSuggestionsAsync(bool refresh = false, CancellationToken ct = default) =>
        GetAsync<MusicSuggestions>(
            SuggestionsPath,
            refresh ? new[] { new KeyValuePair<string, string>("refresh", "true") } : null,
            ct);

    /// <summary>
    /// "Not interested": with <paramref name="title"/> that one album, without it the whole artist.
    /// <paramref name="dismissed"/> false takes it back. 204.
    /// </summary>
    public Task DismissMusicSuggestionAsync(string artist, string? title = null, bool dismissed = true, CancellationToken ct = default) =>
        SendNoResultAsync(
            $"{SuggestionsPath}/dismiss",
            HttpMethod.Post,
            new MusicSuggestionDismissInput { Artist = artist, Title = string.IsNullOrWhiteSpace(title) ? null : title, Dismissed = dismissed },
            ct);

    /// <summary>Downloads the albums as one imported list named "Suggested for you".</summary>
    public Task<MediaImportJob> DownloadMusicSuggestionsAsync(IEnumerable<MusicSuggestedAlbum> albums, CancellationToken ct = default) =>
        StartMediaImportAsync(MediaImportKind.Music, new MediaImportStartInput
        {
            Name = MusicSuggestionText.ListName,
            Items = albums.Select(a => a.ToImportItem()).ToList(),
        }, ct);
}
