using System.Net.Http.Json;
using Microsoft.Extensions.Options;

namespace BlueIrisOllamaBridge;

// Pings the HomeKit sidecar (core/homekit-bridge) so the camera's motion sensor trips in Apple
// Home, which is what produces the native iPhone notification. Unlike the MQTT action - which
// publishes every event so Home Assistant can build its own history - this one only fires for
// alert-worthy events: a HomeKit motion sensor has nowhere to put "nothing interesting happened",
// and every trip is a push notification on someone's phone.
public sealed class HomeKitAlertAction : IAlertAction
{
    private readonly HttpClient _httpClient;
    private readonly IOptionsMonitor<HomeKitOptions> _optionsMonitor;
    private readonly ILogger<HomeKitAlertAction> _logger;

    public HomeKitAlertAction(HttpClient httpClient, IOptionsMonitor<HomeKitOptions> options, ILogger<HomeKitAlertAction> logger)
    {
        _httpClient = httpClient;
        _optionsMonitor = options;
        _logger = logger;
    }

    public async Task ExecuteAsync(AlertResult result, CancellationToken cancellationToken)
    {
        var options = _optionsMonitor.CurrentValue;
        if (!options.Enabled || result.Analysis is null) return;

        if (!AlertNotificationPolicy.ShouldNotify(result)) return;

        var url = $"{options.SidecarBaseUrl.TrimEnd('/')}/api/trigger";

        try
        {
            using var response = await _httpClient.PostAsJsonAsync(
                url, new { camera = result.Camera }, cancellationToken);

            if (!response.IsSuccessStatusCode)
            {
                var body = await response.Content.ReadAsStringAsync(cancellationToken);
                _logger.LogWarning("HomeKit sidecar rejected the motion trigger: {StatusCode} {Body}",
                    (int)response.StatusCode, body);
                return;
            }

            _logger.LogInformation("Triggered HomeKit motion for {AlertId} on camera {Camera}", result.Id, result.Camera);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException && !cancellationToken.IsCancellationRequested)
        {
            // The sidecar is a separate process that may be stopped, restarting, or not installed
            // at all even with HomeKit switched on. AlertProcessor already isolates each action,
            // but swallowing this here keeps a missing optional integration from filling the log
            // with errors for something the rest of the pipeline doesn't care about.
            _logger.LogWarning(ex, "Could not reach the HomeKit sidecar at {Url} - skipping this alert.", url);
        }
    }
}
