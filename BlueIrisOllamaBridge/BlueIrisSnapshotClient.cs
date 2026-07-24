using Microsoft.Extensions.Options;

namespace BlueIrisOllamaBridge;

// Pulls a live JPEG straight from Blue Iris's own HTTP image endpoint, rather than the
// camera directly - Blue Iris already holds the connection/credentials for the camera,
// so this avoids this app needing to speak a second, camera-specific protocol.
public interface IBlueIrisSnapshotClient
{
    Task<byte[]?> GetSnapshotAsync(CancellationToken cancellationToken);
}

public sealed class BlueIrisSnapshotClient : IBlueIrisSnapshotClient
{
    private readonly HttpClient _httpClient;
    private readonly BlueIrisOptions _options;
    private readonly ILogger<BlueIrisSnapshotClient> _logger;

    public BlueIrisSnapshotClient(HttpClient httpClient, IOptions<BlueIrisOptions> options, ILogger<BlueIrisSnapshotClient> logger)
    {
        _httpClient = httpClient;
        _options = options.Value;
        _logger = logger;
    }

    public async Task<byte[]?> GetSnapshotAsync(CancellationToken cancellationToken)
    {
        if (!_options.Enabled || string.IsNullOrWhiteSpace(_options.BaseUrl) || string.IsNullOrWhiteSpace(_options.Camera))
            return null;

        var url = BuildSnapshotUrl(_options);
        try
        {
            using var response = await _httpClient.GetAsync(url, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning("Blue Iris snapshot request failed: {StatusCode}", (int)response.StatusCode);
                return null;
            }

            return await response.Content.ReadAsByteArrayAsync(cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to fetch a live snapshot from Blue Iris");
            return null;
        }
    }

    public static string BuildSnapshotUrl(BlueIrisOptions options)
    {
        var url = $"{options.BaseUrl.TrimEnd('/')}/image/{Uri.EscapeDataString(options.Camera)}";

        var query = new List<string>();
        if (!string.IsNullOrWhiteSpace(options.Username))
            query.Add($"user={Uri.EscapeDataString(options.Username)}");
        if (!string.IsNullOrWhiteSpace(options.Password))
            query.Add($"pw={Uri.EscapeDataString(options.Password)}");

        return query.Count > 0 ? $"{url}?{string.Join('&', query)}" : url;
    }
}
