namespace BlueIrisOllamaBridge;

public static class QuietHoursPolicy
{
    public static bool IsActive(QuietHoursOptions options, DateTimeOffset at)
    {
        if (!options.Enabled) return false;
        if (options.StartHour == options.EndHour) return false;

        var hour = at.ToLocalTime().Hour;
        return options.StartHour < options.EndHour
            ? hour >= options.StartHour && hour < options.EndHour
            : hour >= options.StartHour || hour < options.EndHour;
    }

    // The higher of the channel's own minimum confidence and the quiet-hours minimum, so this
    // can only raise the bar during the window, never lower it below what was already configured.
    public static double EffectiveMinimumConfidence(QuietHoursOptions options, double channelMinimumConfidence, DateTimeOffset at) =>
        IsActive(options, at) ? Math.Max(channelMinimumConfidence, options.MinimumConfidence) : channelMinimumConfidence;
}
