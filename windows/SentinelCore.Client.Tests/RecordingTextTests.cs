using SentinelCore.Client.Models;
using Xunit;

namespace SentinelCore.Client.Tests;

// What somebody is told after stopping a recording. Worth pinning because the wording is the only
// evidence they get that anything was kept: the old message claimed a save while the file was still
// being written, and the whole point of this one is that it reports what the server actually did.
public sealed class RecordingTextTests
{
    private static ManualRecordingStopped Stopped(string? status, int? seconds = null, string detail = "") =>
        new() { Success = true, Status = status, Seconds = seconds, Detail = detail };

    [Fact]
    public void A_saved_recording_says_how_much_video_it_kept()
    {
        Assert.Equal("Saved 42s to Recordings.", RecordingText.StopMessage(Stopped("complete", 42)));
        Assert.Equal("Saved 2m 5s to Recordings.", RecordingText.StopMessage(Stopped("complete", 125)));
    }

    // Partial is a real recording that stopped early - the disk filled, or the camera dropped out -
    // and it reads the same as a complete one on purpose. The footage is what somebody wanted; the
    // reason it is short belongs on the row, not over the video.
    [Fact]
    public void Partial_footage_is_reported_as_kept_rather_than_as_a_problem()
    {
        Assert.Equal("Saved 1m 0s to Recordings.", RecordingText.StopMessage(Stopped("partial", 60)));
    }

    [Fact]
    public void A_recording_that_captured_nothing_says_why_when_the_server_said()
    {
        Assert.Equal(
            "Not enough free disk space to record.",
            RecordingText.StopMessage(Stopped("failed", detail: "Not enough free disk space to record.")));

        Assert.Equal("Nothing was recorded.", RecordingText.StopMessage(Stopped("failed")));
    }

    // No status means the stop found nothing running, because another device had already stopped
    // it. Claiming a save here would be inventing a file this press had nothing to do with.
    [Fact]
    public void Stopping_something_already_stopped_does_not_claim_to_have_saved_anything()
    {
        Assert.Equal("Recording stopped.", RecordingText.StopMessage(Stopped(null)));
    }

    // A recording stopped before ffmpeg closed its first segment has a file but no measurable
    // length. Better a vaguer true sentence than "Saved 0s".
    [Fact]
    public void A_recording_with_no_measured_length_still_reads_as_saved()
    {
        Assert.Equal("Recording saved.", RecordingText.StopMessage(Stopped("complete")));
        Assert.Equal("Recording saved.", RecordingText.StopMessage(Stopped("complete", 0)));
    }
}
