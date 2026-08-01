namespace SentinelCore.Web;

// The on-disk convention for trigger snapshots, in one place because more than one thing writes
// and reads them now: TriggerZoneMonitor's per-camera ffmpeg session continuously overwrites
// _latest.jpg here, and both it and CameraTriggerService save each fired trigger's full-resolution
// frame into the same per-camera folder - so an externally-triggered camera lands its images
// exactly where the Triggers tab and the day timeline already look for them.
public static class TriggerImagePaths
{
    public static string ResolveRoot(IConfiguration configuration, IHostEnvironment environment)
    {
        var configured = configuration["CamerasDataDirectory"] ?? "data";
        var root = Path.IsPathRooted(configured) ? configured : Path.Combine(environment.ContentRootPath, configured);
        return Path.Combine(root, "trigger-images");
    }

    public static string CameraDirectory(string root, Guid cameraId) =>
        Path.Combine(root, cameraId.ToString("N"));

    public static string LatestSnapshot(string root, Guid cameraId) =>
        Path.Combine(CameraDirectory(root, cameraId), "_latest.jpg");

    // Reads the continuously-updated full-resolution frame ffmpeg is already writing for this
    // camera, rather than opening a brand-new RTSP connection - which can take several seconds to
    // negotiate and lands on a frame taken well after the moment that caused the trigger. A couple
    // of retries cover the rare case where a read arrives mid-write; null covers the file not
    // existing yet, and the caller falls back to a live capture.
    //
    // maxAge is for callers that don't own the ffmpeg session writing this file. A zone that was
    // switched off leaves its last _latest.jpg behind forever, and handing that back would answer
    // a trigger with a frame from days ago; TriggerZoneMonitor passes null because its own session
    // is by definition writing the file as it reads it.
    public static async Task<byte[]?> TryReadLatestAsync(
        string root, Guid cameraId, TimeSpan? maxAge, CancellationToken cancellationToken)
    {
        var path = LatestSnapshot(root, cameraId);
        for (var attempt = 0; attempt < 3; attempt++)
        {
            try
            {
                if (File.Exists(path) && !IsStale(path, maxAge))
                {
                    var bytes = await File.ReadAllBytesAsync(path, cancellationToken);
                    if (IsCompleteJpeg(bytes)) return bytes;
                }
            }
            catch (IOException)
            {
                // ffmpeg may have the file open for writing at this exact instant; retry.
            }

            await Task.Delay(50, cancellationToken);
        }

        return null;
    }

    private static bool IsStale(string path, TimeSpan? maxAge) =>
        maxAge is { } age && DateTime.UtcNow - File.GetLastWriteTimeUtc(path) > age;

    public static bool IsCompleteJpeg(byte[] bytes) =>
        bytes.Length > 4 &&
        bytes[0] == 0xFF && bytes[1] == 0xD8 &&
        bytes[^2] == 0xFF && bytes[^1] == 0xD9;
}
