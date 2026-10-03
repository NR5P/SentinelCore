namespace SentinelCore.Web;

// TEMPORARY: stand-ins for the Music and Podcasts halves until they are merged. Delete this
// file and the AUDIO-TEMPORARY registrations in Program.cs when merging them.
public sealed class TemporaryMusicAudioSource : IMusicAudioSource
{
    public Task<bool> EnabledAsync(CancellationToken cancellationToken) => Task.FromResult(false);
    public Task<IReadOnlyList<AudioEntry>> ResolveTracksAsync(string userKey, bool isAdmin, IReadOnlyList<Guid> ids, CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<AudioEntry>>([]);
    public Task<IReadOnlyList<Guid>> QueueForSourceAsync(string userKey, string type, string? id, CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<Guid>>([]);
    public Task<object> StatsAsync(string userKey, DateTimeOffset from, DateTimeOffset to, CancellationToken cancellationToken) => Task.FromResult<object>(new { });
    public Task<IReadOnlyList<AudioLibraryInfo>> LibrariesAsync(CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<AudioLibraryInfo>>([]);
    public void RequestScan(Guid libraryId) { }
    public Task<bool> HasAlbumAsync(string artist, string album, CancellationToken cancellationToken) => Task.FromResult(false);
    public Task<double> LosslessShareAsync(CancellationToken cancellationToken) => Task.FromResult(0.0);
    public Task<MusicBackup> ExportAsync(CancellationToken cancellationToken) => Task.FromResult(new MusicBackup());
    public Task<int> RestoreAsync(MusicBackup backup, CancellationToken cancellationToken) => Task.FromResult(0);
}

public sealed class TemporaryPodcastAudioSource : IPodcastAudioSource
{
    public Task<bool> EnabledAsync(CancellationToken cancellationToken) => Task.FromResult(false);
    public Task<IReadOnlyList<AudioEntry>> ResolveEpisodesAsync(string userKey, bool isAdmin, IReadOnlyList<Guid> ids, CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<AudioEntry>>([]);
    public Task<IReadOnlyList<AudioEntry>> BriefingEpisodesAsync(string userKey, int max, CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<AudioEntry>>([]);
    public Task<IReadOnlyList<Guid>> QueueForSourceAsync(string userKey, string type, string? id, CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<Guid>>([]);
    public Task<object> StatsAsync(string userKey, DateTimeOffset from, DateTimeOffset to, CancellationToken cancellationToken) => Task.FromResult<object>(new { });
    public Task<PodcastBackup> ExportAsync(CancellationToken cancellationToken) => Task.FromResult(new PodcastBackup());
    public Task<int> RestoreAsync(PodcastBackup backup, CancellationToken cancellationToken) => Task.FromResult(0);
}
