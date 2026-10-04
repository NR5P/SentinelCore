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

    // How long one message may sit unwritten to the far side before the MSE bridge is torn down.
    //
    // This is not a latency budget, it is a "the peer is gone" test, and it exists because the
    // failure it catches is silent and expensive. A viewer that stops draining its socket - an
    // Apple TV that slept, a tab the OS froze - leaves the send blocked forever, so this pump
    // stops reading from go2rtc, go2rtc's socket to us fills, and go2rtc stops. But go2rtc only
    // runs a producer while it has a consumer, and the wedged consumer still counts, so the
    // camera keeps being pulled at full bitrate on behalf of nobody, indefinitely.
    //
    // Comfortably longer than any client's own stall watchdog (6s on the Apple apps, 10s on the
    // dashboard's MSE rung), so a session torn down here is always one the viewer has already
    // given up on and reconnected past. That ordering is the point: this can only ever collect
    // sessions that are already dead, never interrupt one somebody is watching.
    private static readonly TimeSpan MseWriteTimeout = TimeSpan.FromSeconds(30);

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

            return Results.Ok(cameras.Select(Masked));
        }).RequireAuthorization();

        // The one way to get a camera's real password, deliberately separated from the list above.
        //
        // Admin-only, and one camera at a time on an explicit request, because that is the whole
        // difference: the list is fetched by every client on every launch and every pull-to-
        // refresh, by anyone allowed to watch video at all. A Viewer account exists to do exactly
        // that and nothing else, so handing it the credentials to the cameras themselves - which
        // on most makes it an ONVIF admin, and which people reuse elsewhere - was never something
        // watching live video needed.
        //
        // Note this is only as closed as the install is: with login not yet enabled,
        // AuthGateMiddleware substitutes a synthetic Admin and anything on the LAN can still ask.
        // Turning login on is what shuts that door; this makes sure that once it is on, the door
        // is actually in the wall.
        app.MapGet("/api/cameras/{id:guid}/password", async (Guid id, ICameraStore store, CancellationToken ct) =>
            await store.GetAsync(id, ct) is { } camera
                ? Results.Ok(new { password = camera.Password ?? string.Empty })
                : Results.NotFound())
            .RequireAuthorization("AdminOnly");

        app.MapPost("/api/cameras", async (CameraInputDto dto, ICameraStore store, CancellationToken ct) =>
        {
            var errors = Validate(dto);
            if (errors.Count > 0) return Results.BadRequest(new { errors });

            var camera = await store.AddAsync(
                dto.Name.Trim(), dto.StreamUrl.Trim(), dto.Username, dto.Password, dto.IncludeAudio,
                dto.PtzEnabled, dto.OnvifPtzServiceUrl, dto.OnvifProfileToken, dto.IsBrowserPublished, ct);
            return Results.Ok(Masked(camera));
        }).RequireAuthorization("AdminOnly");

        app.MapPut("/api/cameras/{id:guid}", async (Guid id, CameraInputDto dto, ICameraStore store, HlsStreamManager streams, Go2RtcManager webrtc, CancellationToken ct) =>
        {
            var errors = Validate(dto);
            if (errors.Count > 0) return Results.BadRequest(new { errors });

            // An edit form that loaded the masked camera and changed only the name sends the mask
            // straight back; that means "keep the stored password", never "set the password to
            // eight bullet characters". Same contract as a masked settings secret - see
            // SettingsSecrets.UnmaskAsync.
            //
            // One read serves both halves: StreamUrl now arrives masked too (see Masked), and a
            // form that changed only the host has to keep the stored secret while taking the new
            // host - so the URL is restored piece by piece rather than kept or replaced whole.
            // Absent means "leave this alone", empty means "clear it". The two are distinct on the
            // way in - null is a field the caller never sent, "" is a box the user emptied on
            // purpose - and collapsing them is how a camera silently loses the login it needs.
            // A gate camera did exactly that: an edit that carried neither field wrote NULL over
            // both, and the camera answered every RTSP DESCRIBE with 401 until they were retyped.
            // Erring toward keeping a credential costs a deliberate clear one explicit ""; erring
            // the other way costs a working camera, with nothing on screen to say why.
            var existing = await store.GetAsync(id, ct);
            var password = dto.Password switch
            {
                null => existing?.Password,
                var p when p == SettingsSecrets.Mask => existing?.Password,
                _ => dto.Password
            };
            var username = dto.Username ?? existing?.Username;
            var streamUrl = StreamUrlSecrets.Unmask(dto.StreamUrl.Trim(), existing?.StreamUrl);

            var updated = await store.UpdateAsync(
                id, dto.Name.Trim(), streamUrl, username, password, dto.IncludeAudio,
                dto.PtzEnabled, dto.OnvifPtzServiceUrl, dto.OnvifProfileToken, dto.IsBrowserPublished, ct);
            if (!updated) return Results.NotFound();

            // Source may have changed - restart the stream on next view rather than serving stale settings.
            streams.StopStream(id);
            webrtc.ForgetStream(id);
            return Results.Ok(new { saved = true });
        }).RequireAuthorization("AdminOnly");

        // Deleting a camera takes its per-camera *configuration* with it. Without that, the rows
        // outlive the camera and stay switched on: a trigger config for a camera that no longer
        // exists survives every restart, is written into config exports and restored from them, and
        // comes back to life on the day some other camera is added under a reused id. The gate
        // camera replaced on 2026-08-27 left exactly such a row behind.
        //
        // Configuration only. Recordings and trigger events - the footage and the images - are
        // history rather than settings, and deleting the camera is not a statement about what it
        // saw. They stay, and the Triggers and Recordings tabs go on showing them.
        app.MapDelete("/api/cameras/{id:guid}", async (
            Guid id, ICameraStore store, HlsStreamManager streams, Go2RtcManager webrtc,
            ITriggerConfigStore triggerConfigs, IGateCheckStore gateChecks,
            IRecordingScheduleStore recordingSchedules, CancellationToken ct) =>
        {
            streams.StopStream(id);
            webrtc.ForgetStream(id);
            var deleted = await store.DeleteAsync(id, ct);
            if (!deleted) return Results.NotFound();

            // After the camera row is gone, not before: every one of these is safe to run twice and
            // harmless when there was nothing to remove, whereas a camera left behind by a cleanup
            // that failed half way would be a camera whose settings had been silently emptied.
            await triggerConfigs.DeleteAsync(id, ct);
            await gateChecks.DeleteAsync(id, ct);
            foreach (var schedule in await recordingSchedules.GetForCameraAsync(id, ct))
                await recordingSchedules.DeleteAsync(schedule.Id, ct);

            return Results.Ok(new { deleted = true });
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
        app.MapPost("/api/cameras/{id:guid}/webrtc", (Guid id, HttpContext http, ICameraStore store, Go2RtcManager webrtc, CancellationToken ct) =>
                ServeWebRtcAsync(id, StreamQuality.Main, http, store, webrtc, ct))
            .RequireAuthorization();

        app.MapPost("/api/cameras/{id:guid}/webrtc/{quality:regex(^(main|sub)$)}",
            (Guid id, string quality, HttpContext http, ICameraStore store, Go2RtcManager webrtc, CancellationToken ct) =>
                ServeWebRtcAsync(id, ParseQuality(quality), http, store, webrtc, ct))
            .RequireAuthorization();

        static async Task<IResult> ServeWebRtcAsync(
            Guid id, StreamQuality quality, HttpContext http, ICameraStore store, Go2RtcManager webrtc, CancellationToken ct)
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
                var answer = await webrtc.ExchangeOfferAsync(camera, offer, quality, ct);
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
        }

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
        app.Map("/api/cameras/{id:guid}/mse", (Guid id, HttpContext http, ICameraStore store, Go2RtcManager webrtc, ILoggerFactory loggerFactory, CancellationToken ct) =>
                ServeMseAsync(id, StreamQuality.Main, http, store, webrtc, loggerFactory, ct))
            .RequireAuthorization();

        // Same two-route shape as the HLS playlist above, and for the same reason: an app built
        // before this existed keeps asking for the unqualified path and keeps getting the main
        // stream, while a grid of tiles asks for the cheap rendition by name.
        // MSE alone also takes "hd" (see StreamQuality.Hd) - the HLS and WebRTC routes don't.
        app.Map("/api/cameras/{id:guid}/mse/{quality:regex(^(main|sub|hd)$)}",
            (Guid id, string quality, HttpContext http, ICameraStore store, Go2RtcManager webrtc, ILoggerFactory loggerFactory, CancellationToken ct) =>
                ServeMseAsync(id, ParseQuality(quality), http, store, webrtc, loggerFactory, ct))
            .RequireAuthorization();

        static async Task<IResult> ServeMseAsync(
            Guid id, StreamQuality quality, HttpContext http, ICameraStore store, Go2RtcManager webrtc,
            ILoggerFactory loggerFactory, CancellationToken ct)
        {
            if (!http.WebSockets.IsWebSocketRequest)
                return Results.BadRequest(new { errors = new[] { "Expected a WebSocket request." } });

            var camera = await store.GetAsync(id, ct);
            if (camera is null) return Results.NotFound();

            System.Net.WebSockets.ClientWebSocket upstream;
            try
            {
                upstream = await webrtc.ConnectMseAsync(camera, quality, ct);
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
                var toBrowser = PumpWebSocketAsync(upstream, browser, MseWriteTimeout, linked.Token);
                var toUpstream = PumpWebSocketAsync(browser, upstream, MseWriteTimeout, linked.Token);
                await Task.WhenAny(toBrowser, toUpstream);
                linked.Cancel();
                try { await Task.WhenAll(toBrowser, toUpstream); } catch { /* one side went away - expected */ }

                // Worth a warning rather than silence: until this teardown existed the session
                // simply stayed, and a stuck viewer held a go2rtc consumer - and so a camera being
                // pulled at full bitrate - for as long as the service ran. If this line starts
                // appearing in a tight loop it means a viewer is reconnecting into the same wedge,
                // which is a different problem to the one this timeout solves.
                if (toBrowser.IsCompletedSuccessfully && toBrowser.Result)
                {
                    loggerFactory.CreateLogger("SentinelCore.Web.LiveView").LogWarning(
                        "MSE bridge for {Camera} torn down: the viewer stopped reading for {Timeout:0}s, which was holding a go2rtc consumer open.",
                        camera.Name, MseWriteTimeout.TotalSeconds);
                }
            }

            return Results.Empty;
        }

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

    // The {quality} route segment, which the route constraint has already limited to main|sub
    // (or main|sub|hd on the MSE route).
    private static StreamQuality ParseQuality(string quality) =>
        quality.Equals("sub", StringComparison.OrdinalIgnoreCase) ? StreamQuality.Sub
        : quality.Equals("hd", StringComparison.OrdinalIgnoreCase) ? StreamQuality.Hd
        : StreamQuality.Main;

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

        return Results.File(segmentPath, SegmentContentType(file));
    }

    // An H.265 camera's stream is segmented as fragmented MP4 rather than MPEG-TS (see
    // HlsStreamManager.BuildFfmpegArguments), and its playlist additionally points every player at
    // an init.mp4 to be fetched before the first segment. Both arrive on this route, and handing
    // them back as video/mp2t is the kind of mislabelling a strict player refuses outright.
    private static string SegmentContentType(string file) =>
        Path.GetExtension(file).ToLowerInvariant() switch
        {
            ".m4s" or ".mp4" => "video/mp4",
            _ => "video/mp2t"
        };

    // One direction of the MSE bridge: copy messages (type and message boundaries preserved)
    // until the source closes or errors. 64KB fits go2rtc's fMP4 segments comfortably; larger
    // messages arrive in parts and are forwarded part by part with EndOfMessage carried through.
    // Returns true if this direction was torn down because the far side stopped reading, which the
    // caller logs. Every other ending - either peer closing, the request being cancelled - is the
    // ordinary way an MSE session finishes and is silent.
    //
    // Public, and taking its deadline as an argument rather than reading MseWriteTimeout directly,
    // purely for testability - this project has no InternalsVisibleTo, and the behaviour worth
    // pinning down is a 30-second wait that no test should actually sit through.
    public static async Task<bool> PumpWebSocketAsync(
        System.Net.WebSockets.WebSocket from, System.Net.WebSockets.WebSocket to,
        TimeSpan writeTimeout, CancellationToken cancellationToken)
    {
        var buffer = new byte[64 * 1024];
        var writeDeadlineExpired = false;
        try
        {
            while (from.State == System.Net.WebSockets.WebSocketState.Open)
            {
                var result = await from.ReceiveAsync(buffer, cancellationToken);
                if (result.MessageType == System.Net.WebSockets.WebSocketMessageType.Close)
                {
                    await to.CloseOutputAsync(
                        System.Net.WebSockets.WebSocketCloseStatus.NormalClosure, null, cancellationToken);
                    return false;
                }

                // Deadline per message, not per session: a healthy stream completes every one of
                // these in microseconds, so the timer only ever expires on a peer that has stopped
                // reading altogether. Cancelling the send aborts the socket, which is what makes
                // the caller's WhenAny return and the whole bridge unwind - see MseWriteTimeout.
                using var writeDeadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                writeDeadline.CancelAfter(writeTimeout);
                try
                {
                    await to.SendAsync(
                        new ArraySegment<byte>(buffer, 0, result.Count), result.MessageType, result.EndOfMessage, writeDeadline.Token);
                }
                // Only our own deadline, never the caller's cancellation: a shutdown or a viewer
                // closing the tab cancels the outer token too, and neither of those is this fault.
                catch (Exception ex) when (
                    ex is OperationCanceledException or System.Net.WebSockets.WebSocketException
                    && writeDeadline.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
                {
                    writeDeadlineExpired = true;
                    return true;
                }
            }
        }
        catch (Exception ex) when (ex is OperationCanceledException or System.Net.WebSockets.WebSocketException)
        {
            // Either peer vanishing mid-stream is the normal way an MSE session ends - a closed
            // tab doesn't say goodbye. The caller closes the other side.
        }

        return writeDeadlineExpired;
    }

    // A camera's password is a real credential to a real device on the network - usually the
    // camera's admin login, often reused - so it travels the same way a stored SMTP password or
    // bot token does: replaced by a fixed mask on the way out, understood as "unchanged" on the
    // way back in. Reusing SettingsSecrets.Mask rather than inventing a second marker means every
    // client already knows how to render this and what sending it back means.
    //
    // Empty stays empty, so a form can still tell "no password set" from "one is set, and it is
    // not being shown to you" - the same distinction SettingsSecrets.MaskInPlace preserves.
    //
    // StreamUrl gets the same treatment, because on this network it holds a second copy of the very
    // same credential: Reolink cameras cannot use RTSP here, so they stream over HTTP-FLV with
    // "&password=<secret>" in the query string. Masking only the column left that copy going out in
    // full to every signed-in caller, which made the admin-only /api/cameras/{id}/password endpoint
    // a door with a second one open beside it. See StreamUrlSecrets.
    //
    // The config backup deliberately does not go through here: it reads ICameraStore directly, and
    // a backup whose cameras could not reconnect after a restore would not be a backup. That file
    // is already documented as being as sensitive as appsettings.json.
    private static Camera Masked(Camera camera) => camera with
    {
        Password = string.IsNullOrEmpty(camera.Password) ? camera.Password : SettingsSecrets.Mask,
        StreamUrl = StreamUrlSecrets.Mask(camera.StreamUrl),
    };

    // internal so config restore can hold imported cameras to the same rules this endpoint
    // enforces - see BackupEndpoints.Validate.
    internal static List<string> Validate(CameraInputDto dto)
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
