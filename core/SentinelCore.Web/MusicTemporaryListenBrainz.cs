namespace SentinelCore.Web;

// TEMPORARY: stands in for AudioListenBrainz (the shared audio half, built on another branch) so
// the Music tab can hand it plays in this branch. Delete this file, and its registration in
// Program.cs, when the two are merged.
public sealed class MusicTemporaryListenBrainz : IListenBrainzSubmitter
{
    public void Listened(string userKey, AudioListen listen) { }

    public void PlayingNow(string userKey, AudioListen listen) { }
}
