namespace SentinelCore;

// Matches a plate the AI transcribed against the configured KnownVehicles list. Comparison is
// normalized (letters/digits only, case-insensitive) since OCR from a general vision model - and
// plates themselves - vary in spacing, dashes, and state-specific formatting.
public static class KnownVehicleMatcher
{
    public static string? Match(IReadOnlyList<KnownVehicle> knownVehicles, string? plateText)
    {
        var normalized = Normalize(plateText);
        if (normalized.Length == 0) return null;

        foreach (var vehicle in knownVehicles)
        {
            if (Normalize(vehicle.Plate) == normalized)
                return vehicle.Label;
        }

        return null;
    }

    private static string Normalize(string? plate) =>
        new string((plate ?? string.Empty).Where(char.IsLetterOrDigit).ToArray()).ToUpperInvariant();
}
