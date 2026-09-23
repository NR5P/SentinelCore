namespace SentinelCore.Web;

// Signaling for a camera that *is* a device: a laptop or spare phone somewhere in the house
// opens the publisher page - or the Sentinel Core app on a phone, Mac or PC does the same thing
// natively - and its camera becomes a camera in this dashboard. The mirror image of the live-view
// WebRTC endpoint in CameraEndpoints - same SDP exchange, opposite direction. Every client speaks
// exactly this: an SDP offer up as application/sdp, go2rtc's answer back, then a status poll.
//
// Why this exists at all: every other camera here is pulled, and a webcam has no address to pull.
// Rather than teach the pipeline about a second kind of source, the publisher's video is pushed
// into the go2rtc sidecar and read back out over loopback RTSP (see CameraSource), so from
// HlsStreamManager and TriggerZoneMonitor down, a published camera is indistinguishable from a
// real one - it records clips, fires trigger zones, and gets analyzed the same way.
//
// Auth is the dashboard's own: publishing is just another authenticated action, so the device
// doing it has to be logged in like any other. That deliberately avoids minting a standing
// publish credential - a token that lets its holder inject video into a security system is a
// worse thing to leak than a session.
public static class CameraPublishEndpoints
{
    public static void MapCameraPublishEndpoints(this WebApplication app)
    {
        // The publishing browser POSTs its SDP offer here and gets go2rtc's answer, after which
        // its camera feeds the sidecar directly and this server is out of the media path.
        //
        // AdminOnly, unlike live view: this writes video into the system rather than reading it,
        // and anyone who can publish to a camera can decide what that camera appears to see.
        app.MapPost("/api/cameras/{id:guid}/publish", async (
            Guid id, HttpContext http, ICameraStore store, Go2RtcManager webrtc, CancellationToken ct) =>
        {
            var camera = await store.GetAsync(id, ct);
            if (camera is null) return Results.NotFound();
            if (!camera.IsBrowserPublished)
                return Results.BadRequest(new { errors = new[] { "This camera has its own stream URL - it is not published from a device." } });

            string offer;
            using (var reader = new StreamReader(http.Request.Body))
                offer = await reader.ReadToEndAsync(ct);
            if (string.IsNullOrWhiteSpace(offer))
                return Results.BadRequest(new { errors = new[] { "Expected an SDP offer in the request body." } });

            try
            {
                var answer = await webrtc.ExchangePublishOfferAsync(camera, offer, ct);
                return Results.Text(answer, "application/sdp");
            }
            catch (Go2RtcUnavailableException ex)
            {
                // Unlike live view, there is no fallback tier to drop to - go2rtc is the only way
                // in for a published camera, so this is a dead end the page has to explain.
                return Results.Problem(ex.Message, statusCode: 503);
            }
            catch (Exception ex) when (ex is InvalidOperationException or HttpRequestException or TaskCanceledException)
            {
                return Results.Problem(ex.Message, statusCode: 502);
            }
        }).RequireAuthorization("AdminOnly");

        // Whether a device is currently publishing to this camera. The publisher page polls it to
        // confirm its own video is actually arriving (a peer connection that reports "connected"
        // is not proof the server is receiving frames), and the dashboard uses it to explain an
        // empty tile as "nothing is publishing" rather than a broken camera.
        //
        // lastSeenAt / missingSince come from PublisherWatchdog, so an empty tile can also say
        // "last had video at 3:12" - the difference between a laptop that just went to sleep and
        // one that was never started.
        app.MapGet("/api/cameras/{id:guid}/publish/status", async (
            Guid id, ICameraStore store, Go2RtcManager webrtc, PublisherWatchdog watchdog, CancellationToken ct) =>
        {
            var camera = await store.GetAsync(id, ct);
            if (camera is null) return Results.NotFound();
            if (!camera.IsBrowserPublished)
                return Results.BadRequest(new { errors = new[] { "This camera is not published from a device." } });

            var live = await webrtc.HasLivePublisherAsync(id, ct);
            var seen = watchdog.Presence.Get(id);
            return Results.Ok(new
            {
                publishing = live,
                lastSeenAt = live ? DateTimeOffset.UtcNow : seen?.LastLiveAt,
                missingSince = live ? null : seen?.MissingSince
            });
        }).RequireAuthorization();
    }
}
