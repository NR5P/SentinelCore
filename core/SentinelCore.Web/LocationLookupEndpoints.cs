using System.Text.Json;

namespace SentinelCore.Web;

// Turns "Louisville, KY" into a latitude and longitude, so nobody has to go and find their own
// coordinates to set up a schedule that follows the sun.
//
// This is the one part of the location feature that leaves the building, and it does so only
// when someone presses Find: it sends the place name they typed to OpenStreetMap's public
// geocoder and returns the candidates. Nothing is sent automatically, nothing about the install
// goes with it, and the result is only ever used to fill in two numbers the user can also just
// type. The sunrise calculation itself never touches the network.
//
// A LAN-only server will get a clear "couldn't reach it, type the coordinates instead" rather
// than a hang - which is why manual entry stays the primary path in the Settings form.
public sealed class LocationLookupLog;

public static class LocationLookupEndpoints
{
    private const string NominatimSearchUrl = "https://nominatim.openstreetmap.org/search";

    // Nominatim's usage policy requires a real identifying User-Agent, and refuses requests
    // without one. Sending the product name and nothing else keeps that promise without
    // identifying the install.
    private const string UserAgent = "SentinelCore/1.0 (self-hosted camera alert system)";

    private const int MaximumResults = 5;

    public static void MapLocationLookupEndpoints(this WebApplication app)
    {
        // AdminOnly to match the rest of /api/settings, of which this is a helper.
        app.MapGet("/api/settings/location/lookup", async (
            string? query,
            IHttpClientFactory httpClientFactory,
            ILogger<LocationLookupLog> logger,
            CancellationToken ct) =>
        {
            if (string.IsNullOrWhiteSpace(query))
                return Results.BadRequest(new { error = "Type a town or city to look up." });

            var client = httpClientFactory.CreateClient(nameof(LocationLookupEndpoints));

            var url = $"{NominatimSearchUrl}?q={Uri.EscapeDataString(query.Trim())}&format=json&limit={MaximumResults}";
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.TryAddWithoutValidation("User-Agent", UserAgent);

            try
            {
                using var response = await client.SendAsync(request, ct);
                if (!response.IsSuccessStatusCode)
                {
                    logger.LogWarning("Place lookup returned {StatusCode}", (int)response.StatusCode);
                    return Results.Json(
                        new { error = "The place lookup service did not answer. Enter the coordinates directly instead." },
                        statusCode: 502);
                }

                var body = await response.Content.ReadAsStringAsync(ct);
                return Results.Ok(new { results = Parse(body) });
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested)
            {
                // Overwhelmingly the LAN-only case, so the message says what to do rather than
                // what went wrong.
                logger.LogInformation(ex, "Place lookup could not reach the geocoding service");
                return Results.Json(
                    new { error = "Couldn't reach the place lookup service - this server may have no internet access. Enter the latitude and longitude directly instead." },
                    statusCode: 502);
            }
        })
        .RequireAuthorization("AdminOnly");
    }

    private static List<LocationCandidate> Parse(string body)
    {
        var candidates = new List<LocationCandidate>();

        using var document = JsonDocument.Parse(body);
        if (document.RootElement.ValueKind != JsonValueKind.Array) return candidates;

        foreach (var element in document.RootElement.EnumerateArray())
        {
            // Nominatim returns the coordinates as strings, and a result missing either is not
            // a result - skipping is better than surfacing a half-filled candidate.
            if (!element.TryGetProperty("lat", out var latitude) ||
                !element.TryGetProperty("lon", out var longitude) ||
                !double.TryParse(latitude.GetString(), out var parsedLatitude) ||
                !double.TryParse(longitude.GetString(), out var parsedLongitude))
                continue;

            var label = element.TryGetProperty("display_name", out var displayName)
                ? displayName.GetString() ?? ""
                : "";

            candidates.Add(new LocationCandidate(label, parsedLatitude, parsedLongitude));
        }

        return candidates;
    }
}

public sealed record LocationCandidate(string Label, double Latitude, double Longitude);
