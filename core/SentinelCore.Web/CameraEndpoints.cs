using System.ComponentModel.DataAnnotations;

namespace SentinelCore.Web;

public sealed class CameraInputDto
{
    [Required]
    public string Name { get; set; } = string.Empty;

    // Not [Required]: a browser-published camera has no URL to dial, because the video is pushed
    // to us rather than fetched. See Validate, which requires this only for the ordinary kind.
    public string StreamUrl { get; set; } = string.Empty;

    // This camera's video comes from a browser pushing its webcam in (a laptop or spare phone
    // used as an indoor camera) rather than from an address this server connects to - see
    // CameraPublishEndpoints.
    public bool IsBrowserPublished { get; set; }

    public string? Username { get; set; }
    public string? Password { get; set; }
    public bool IncludeAudio { get; set; }

    // OnvifPtzServiceUrl/OnvifProfileToken come from a discovery probe (see
    // CameraDiscoveryService/CameraProbeResult) and are just passed through here on save, the
    // same way ProbeResult.StreamUrl already flows into StreamUrl above - the client never makes
    // these up itself. PtzEnabled defaults to whatever the probe detected but can be flipped
    // independently: turning it on without a detected service URL/token just means PTZ calls for
    // this camera will fail with a clear error rather than silently doing nothing.
    public bool PtzEnabled { get; set; }
    public string? OnvifPtzServiceUrl { get; set; }
    public string? OnvifProfileToken { get; set; }
}

// One live tile reporting how it actually got its video. The live grid tries WebRTC, then MSE,
// then HLS (see attachPlayer in cameras.js), and until this existed nothing anywhere recorded
// which of the three a tile ended up on - so "the feed was stuttering last night" was
// unanswerable after the fact, even though the three tiers look completely different on screen
// and the difference between them is most of what "stuttering" means here.
public sealed class LivePathReportDto
{
    // "webrtc" | "mse" | "hls" - the tier this report is about.
    [Required]
    public string Tier { get; set; } = string.Empty;

    // "playing" when frames reached the screen, "failed" when the tile gave up on this tier and
    // dropped to the next one, "degraded" when it hit a stall it expects to recover from in place.
    // The last is what a rebuffering feed looks like from here, and is the whole reason someone
    // reads this log.
    [Required]
    public string Outcome { get; set; } = string.Empty;

    // Why it failed, for a failure. Free text from the client, so it is logged as data and
    // never interpreted.
    public string? Reason { get; set; }

    // Milliseconds from the tile starting this tier to the outcome. The number that says whether
    // a fallback was a real failure or just a deadline that was too tight.
    public int ElapsedMs { get; set; }
}

public static class CameraEndpoints
{
    private static readonly string[] AllowedSchemes = ["rtsp", "http", "https"];

    // Tiers the report endpoint will log. A closed set, so a client bug (or anything else posting
    // here) can't write arbitrary strings into the log.
    private static readonly string[] LiveTiers = ["webrtc", "mse", "hls"];
    private static readonly string[] LiveOutcomes = ["playing", "failed", "degraded"];

    public static void MapCameraEndpoints(this WebApplication app)
    {
        app.MapGet("/api/cameras", async (ICameraStore store, HlsStreamManager streams, CancellationToken ct) =>
        {
            var cameras = await store.GetAllAsync(ct);

            // Fire-and-forget: warm up each camera's stream as soon as something asks for the
            // camera list - app launch, pull-to-refresh, loading the web dashboard - rather than
            // waiting for the Live tab to actually be opened, so ffmpeg has usually already
            // produced segments by the time a viewer gets there. A no-op for cameras already
            // warm (EnsureStreamStartedAsync reuses the running session). CancellationToken.None
            // because this outlives the request that triggered it; failures are swallowed here
            // since the real stream request surfaces them normally.
            // Warmed at Sub rather than Main: a grid of tiles is what opens next in every client,
            // and that is what a grid now asks for. Warming Main instead would hold a full-
            // resolution connection open against every camera to serve nobody - the exact load
            // the low rendition exists to avoid - and on a camera with no low stream Sub resolves
            // to Main anyway, so nothing is left cold. A full-screen viewer pays its own cold
            // start, which is one camera and a couple of seconds.
            foreach (var camera in cameras)
            {
                _ = streams.EnsureStreamStartedAsync(camera, StreamQuality.Sub, CancellationToken.None)
                    .ContinueWith(t => { _ = t.Exception; }, TaskContinuationOptions.OnlyOnFaulted);
            }

            return Results.Ok(cameras);
        }).RequireAuthorization();

        app.MapPost("/api/cameras", async (CameraInputDto dto, ICameraStore store, CancellationToken ct) =>
        {
            var errors = Validate(dto);
            if (errors.Count > 0) return Results.BadRequest(new { errors });

            var camera = await store.AddAsync(
                dto.Name.Trim(), dto.StreamUrl.Trim(), dto.Username, dto.Password, dto.IncludeAudio,
                dto.PtzEnabled, dto.OnvifPtzServiceUrl, dto.OnvifProfileToken, dto.IsBrowserPublished, ct);
            return Results.Ok(camera);
        }).RequireAuthorization("AdminOnly");

        app.MapPut("/api/cameras/{id:guid}", async (Guid id, CameraInputDto dto, ICameraStore store, HlsStreamManager streams, Go2RtcManager webrtc, CancellationToken ct) =>
        {
            var errors = Validate(dto);
            if (errors.Count > 0) return Results.BadRequest(new { errors });

            var updated = await store.UpdateAsync(
                id, dto.Name.Trim(), dto.StreamUrl.Trim(), dto.Username, dto.Password, dto.IncludeAudio,
                dto.PtzEnabled, dto.OnvifPtzServiceUrl, dto.OnvifProfileToken, dto.IsBrowserPublished, ct);
            if (!updated) return Results.NotFound();

            // Source may have changed - restart the stream on next view rather than serving stale settings.
            streams.StopStream(id);
            webrtc.ForgetStream(id);
            return Results.Ok(new { saved = true });
        }).RequireAuthorization("AdminOnly");

        app.MapDelete("/api/cameras/{id:guid}", async (Guid id, ICameraStore store, HlsStreamManager streams, Go2RtcManager webrtc, CancellationToken ct) =>
        {
            streams.StopStream(id);
            webrtc.ForgetStream(id);
            var deleted = await store.DeleteAsync(id, ct);
            return deleted ? Results.Ok(new { deleted = true }) : Results.NotFound();
        }).RequireAuthorization("AdminOnly");

        // Nothing here may be cached, by the client or by anything in front of the server. The
        // playlist is rewritten every segment, so a cached copy hands the player an out-of-date
        // segment list - it either plays that far behind live or asks for segments that have
        // already rolled off. The segments are worse: their names restart at seg_000.ts every time
        // a session does, so a cache holding one from a previous session would feed a viewer video
        // from a completely different point in time. Relevant well beyond the browser, since
        // Internet Exposed Mode puts a Cloudflare Tunnel in this path.
        // Two routes, one handler. The unqualified one is the original and still means the main
        // stream, so a client built before this change - including the copies already installed on
        // phones - keeps working untouched. The qualified one is how a grid asks for the cheap
        // rendition. Quality lives in the path rather than the query because a player resolves the
        // segment names it finds in a playlist relative to the playlist's own URL, and a query
        // string does not survive that: ".../stream/sub/stream.m3u8" yields
        // ".../stream/sub/seg_007.ts", while "?quality=sub" would yield ".../stream/seg_007.ts"
        // and quietly pull main-stream segments.
        app.MapGet("/api/cameras/{id:guid}/stream/{file}",
            (Guid id, string file, HttpContext http, ICameraStore store, HlsStreamManager streams, CancellationToken ct) =>
                ServeHlsAsync(id, StreamQuality.Main, file, http, store, streams, ct))
            .RequireAuthorization();

        app.MapGet("/api/cameras/{id:guid}/stream/{quality:regex(^(main|sub)$)}/{file}",
            (Guid id, string quality, string file, HttpContext http, ICameraStore store, HlsStreamManager streams, CancellationToken ct) =>
                ServeHlsAsync(
                    id,
                    quality.Equals("sub", StringComparison.OrdinalIgnoreCase) ? StreamQuality.Sub : StreamQuality.Main,
                    file, http, store, streams, ct))
            .RequireAuthorization();

        // WebRTC signaling for a live tile: the browser POSTs its SDP offer here and gets go2rtc's
        // answer back, after which media flows browser<->go2rtc directly (DTLS-SRTP on the media
        // port) and this server is out of the path. This is the whole reason the live grid can sit
        // a few hundred milliseconds behind the camera instead of HLS's several seconds.
        //
        // 503 is the deliberate "not on this install" answer - go2rtc missing or unstartable - and
        // the dashboard reacts by attaching the HLS player instead, so a plain install without
        // go2rtc behaves exactly as it did before this endpoint existed.
        app.MapPost("/api/cameras/{id:guid}/webrtc", async (Guid id, HttpContext http, ICameraStore store, Go2RtcManager webrtc, CancellationToken ct) =>
        {
            var camera = await store.GetAsync(id, ct);
            if (camera is null) return Results.NotFound();

            string offer;
            using (var reader = new StreamReader(http.Request.Body))
                offer = await reader.ReadToEndAsync(ct);
            if (string.IsNullOrWhiteSpace(offer))
                return Results.BadRequest(new { errors = new[] { "Expected an SDP offer in the request body." } });

            try
            {
                var answer = await webrtc.ExchangeOfferAsync(camera, offer, ct);
                return Results.Text(answer, "application/sdp");
            }
            catch (Go2RtcUnavailableException ex)
            {
                return Results.Problem(ex.Message, statusCode: 503);
            }
            catch (Exception ex) when (ex is InvalidOperationException or HttpRequestException or TaskCanceledException)
            {
                return Results.Problem(ex.Message, statusCode: 502);
            }
        }).RequireAuthorization();

        // MSE live view: the dashboard opens a WebSocket here and this server bridges it to
        // go2rtc's MSE endpoint on loopback (go2rtc's own API carries no auth, so it is never
        // exposed - this endpoint authenticates and then pumps bytes). The browser sends one
        // JSON message naming the codecs it supports; go2rtc answers with the chosen MIME type
        // and then streams fMP4 segments as binary messages.
        //
        // This exists alongside WebRTC because they fail differently: WebRTC needs its own
        // media port reachable from the viewer (blocked by tunnels, VPNs and strict firewalls),
        // while MSE rides this very HTTP connection - if the dashboard loads, MSE streams. The
        // dashboard tries WebRTC first (lower latency), then this, then HLS.
        app.Map("/api/cameras/{id:guid}/mse", async (Guid id, HttpContext http, ICameraStore store, Go2RtcManager webrtc, CancellationToken ct) =>
        {
            if (!http.WebSockets.IsWebSocketRequest)
                return Results.BadRequest(new { errors = new[] { "Expected a WebSocket request." } });

            var camera = await store.GetAsync(id, ct);
            if (camera is null) return Results.NotFound();

            System.Net.WebSockets.ClientWebSocket upstream;
            try
            {
                upstream = await webrtc.ConnectMseAsync(camera, ct);
            }
            catch (Go2RtcUnavailableException ex)
            {
                return Results.Problem(ex.Message, statusCode: 503);
            }
            catch (Exception ex) when (ex is InvalidOperationException or HttpRequestException or System.Net.WebSockets.WebSocketException)
            {
                return Results.Problem(ex.Message, statusCode: 502);
            }

            using (upstream)
            {
                using var browser = await http.WebSockets.AcceptWebSocketAsync();
                // Both directions run until either side closes; the other is then closed too.
                // Browser->go2rtc carries only the small codec-negotiation JSON, go2rtc->browser
                // carries the media, but the pump is symmetric because go2rtc may also send JSON
                // (codec answer) and a future protocol message shouldn't break this bridge.
                using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct);
                var toBrowser = PumpWebSocketAsync(upstream, browser, linked.Token);
                var toUpstream = PumpWebSocketAsync(browser, upstream, linked.Token);
                await Task.WhenAny(toBrowser, toUpstream);
                linked.Cancel();
                try { await Task.WhenAll(toBrowser, toUpstream); } catch { /* one side went away - expected */ }
            }

            return Results.Empty;
        }).RequireAuthorization();

        // Where the live grid reports which tier each tile landed on - see LivePathReportDto for
        // why. Cheap enough to call on every transition: one small POST per tile per tier change,
        // against a grid that is already fetching a video segment per camera per second.
        //
        // Logged, not stored. The question this answers ("was it WebRTC or HLS at 9pm, and why")
        // is asked of the log alongside the go2rtc and ffmpeg lines that explain it, and a
        // security box should not grow a database table for telemetry about itself.
        app.MapPost("/api/cameras/{id:guid}/live-path", async (
            Guid id, LivePathReportDto report, ICameraStore store, ILoggerFactory loggerFactory, CancellationToken ct) =>
        {
            var tier = report.Tier?.Trim().ToLowerInvariant() ?? string.Empty;
            var outcome = report.Outcome?.Trim().ToLowerInvariant() ?? string.Empty;
            if (!LiveTiers.Contains(tier) || !LiveOutcomes.Contains(outcome))
                return Results.BadRequest(new { errors = new[] { "Unrecognised tier or outcome." } });

            var camera = await store.GetAsync(id, ct);
            if (camera is null) return Results.NotFound();

            // Bounded and stripped of newlines: this is client-supplied text going into a log,
            // and a log line that can contain newlines is a log line that can forge other entries.
            var reason = report.Reason is null
                ? null
                : new string(report.Reason.Where(c => !char.IsControl(c)).Take(200).ToArray());

            var logger = loggerFactory.CreateLogger("SentinelCore.Web.LiveView");
            switch (outcome)
            {
                case "playing":
                    logger.LogInformation(
                        "Live tile for {Camera} is playing over {Tier}, {ElapsedMs}ms to first frame",
                        camera.Name, tier, report.ElapsedMs);
                    break;

                // Warnings, not Information: a fallback moves a viewer to a tier that can be an
                // order of magnitude further behind the camera, and a degrade is a feed visibly
                // stalling. Both are the thing that gets noticed and complained about, so both
                // should be findable without knowing to look.
                case "degraded":
                    logger.LogWarning(
                        "Live tile for {Camera} stalled on {Tier} after {ElapsedMs}ms and is recovering in place: {Reason}",
                        camera.Name, tier, report.ElapsedMs, reason ?? "no reason given");
                    break;

                default:
                    logger.LogWarning(
                        "Live tile for {Camera} gave up on {Tier} after {ElapsedMs}ms: {Reason}",
                        camera.Name, tier, report.ElapsedMs, reason ?? "no reason given");
                    break;
            }

            return Results.NoContent();
        }).RequireAuthorization();

        // Instant poster frame for a camera tile: if there's a warm stream, this is just a file
        // read of the JPEG ffmpeg has been overwriting in the background (see
        // HlsStreamManager.GetThumbnailPath). Only a cold camera nobody has looked at recently
        // falls back to a one-off snapshot grab.
        app.MapGet("/api/cameras/{id:guid}/thumbnail.jpg", async (Guid id, ICameraStore store, HlsStreamManager streams, CancellationToken ct) =>
        {
            var thumbnailPath = streams.GetThumbnailPath(id);
            if (thumbnailPath is not null) return Results.File(thumbnailPath, "image/jpeg");

            var camera = await store.GetAsync(id, ct);
            if (camera is null) return Results.NotFound();

            var bytes = await streams.CaptureSnapshotAsync(camera, ct);
            return bytes is null
                ? Results.Problem("Could not grab a frame from this camera.", statusCode: 502)
                : Results.File(bytes, "image/jpeg");
        }).RequireAuthorization();
    }

    // One direction of the MSE bridge: copy messages (type and message boundaries preserved)
    // until the source closes or errors. 64KB fits go2rtc's fMP4 segments comfortably; larger
    // messages arrive in parts and are forwarded part by part with EndOfMessage carried through.
    private static async Task<IResult> ServeHlsAsync(
        Guid id, StreamQuality quality, string file, HttpContext http,
        ICameraStore store, HlsStreamManager streams, CancellationToken ct)
    {
        http.Response.Headers.CacheControl = "no-store, no-cache, must-revalidate, private";

        if (file == "stream.m3u8")
        {
            var camera = await store.GetAsync(id, ct);
            if (camera is null) return Results.NotFound();

            string playlistPath;
            try
            {
                playlistPath = await streams.EnsureStreamStartedAsync(camera, quality, ct);
            }
            catch (Exception ex)
            {
                return Results.Problem(ex.Message, statusCode: 502);
            }

            return Results.File(playlistPath, "application/vnd.apple.mpegurl");
        }

        streams.Touch(id, quality);
        var segmentPath = streams.GetSegmentPath(id, quality, file);
        if (segmentPath is null || !File.Exists(segmentPath)) return Results.NotFound();

        return Results.File(segmentPath, "video/mp2t");
    }

    private static async Task PumpWebSocketAsync(
        System.Net.WebSockets.WebSocket from, System.Net.WebSockets.WebSocket to, CancellationToken cancellationToken)
    {
        var buffer = new byte[64 * 1024];
        try
        {
            while (from.State == System.Net.WebSockets.WebSocketState.Open)
            {
                var result = await from.ReceiveAsync(buffer, cancellationToken);
                if (result.MessageType == System.Net.WebSockets.WebSocketMessageType.Close)
                {
                    await to.CloseOutputAsync(
                        System.Net.WebSockets.WebSocketCloseStatus.NormalClosure, null, cancellationToken);
                    return;
                }

                await to.SendAsync(
                    new ArraySegment<byte>(buffer, 0, result.Count), result.MessageType, result.EndOfMessage, cancellationToken);
            }
        }
        catch (Exception ex) when (ex is OperationCanceledException or System.Net.WebSockets.WebSocketException)
        {
            // Either peer vanishing mid-stream is the normal way an MSE session ends - a closed
            // tab doesn't say goodbye. The caller closes the other side.
        }
    }

    private static List<string> Validate(CameraInputDto dto)
    {
        var errors = new List<string>();
        if (string.IsNullOrWhiteSpace(dto.Name)) errors.Add("Name is required.");

        // A browser-published camera is defined by the absence of an address: the publishing
        // device connects to us. Rejecting a URL outright rather than ignoring one keeps the two
        // kinds from being silently half-configured - a camera with both would leave "which wins?"
        // to whoever reads BuildStreamSources next.
        if (dto.IsBrowserPublished)
        {
            if (!string.IsNullOrWhiteSpace(dto.StreamUrl))
                errors.Add("A browser-published camera has no stream URL - the device pushes video to this server.");
            return errors;
        }

        if (string.IsNullOrWhiteSpace(dto.StreamUrl))
        {
            errors.Add("Stream URL is required.");
        }
        else if (!Uri.TryCreate(dto.StreamUrl.Trim(), UriKind.Absolute, out var uri) ||
                 !AllowedSchemes.Contains(uri.Scheme, StringComparer.OrdinalIgnoreCase))
        {
            errors.Add("Stream URL must be an absolute rtsp://, http://, or https:// URL.");
        }

        return errors;
    }
}
