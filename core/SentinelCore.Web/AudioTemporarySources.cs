namespace SentinelCore.Web;

// TEMPORARY: a stand-in for the Podcasts half until it is merged. Delete this file and the
// AUDIO-TEMPORARY registration in Program.cs when merging it.
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
