namespace SentinelCore;

// What to do when something fires - shared by a time-driven Schedule and (via
// AutomationRule.ToAction) a detection-driven AutomationRule, so both go through the one
// AutomationActionDispatcher rather than each growing its own copy of "publish MQTT / call a
// webhook / switch a device".
//
// Which fields matter depends on Type: "device" uses DeviceId/DeviceAction/DeviceBrightness,
// "mqtt" uses the Mqtt* fields, "webhook" the Webhook* ones. The unused ones are empty rather
// than null so every storage layer can write them without a null check.
public sealed record AutomationAction(
    string Type,
    Guid? DeviceId,
    string DeviceAction,
    int? DeviceBrightness,
    string MqttTopic,
    string MqttPayload,
    bool MqttRetain,
    string WebhookUrl,
    string WebhookMethod,
    string WebhookHeaders,
    string WebhookBody,
    string WebhookContentType)
{
    public const string TypeDevice = "device";
    public const string TypeMqtt = "mqtt";
    public const string TypeWebhook = "webhook";

    public static readonly IReadOnlyList<string> KnownTypes = [TypeDevice, TypeMqtt, TypeWebhook];

    public static bool IsKnownType(string type) =>
        KnownTypes.Contains(type, StringComparer.OrdinalIgnoreCase);
}

// A "do this at that time" rule. The counterpart to AutomationRule's "do this when the AI sees
// that" - same action vocabulary (see AutomationAction), different trigger.
//
// Which timing fields matter depends on Kind, exactly like AutomationAction's fields depend on
// its Type. See ScheduleTiming.NextOccurrence, which is the only place that interprets them.
//
// LastRunUtc/LastStatus/LastDetail/NextDueUtc/RunCount are denormalized summaries of the
// ScheduleRuns history, kept on the row so listing schedules is one query rather than one query
// plus a per-schedule lookup. ScheduleRuns remains the authoritative record.
public sealed record Schedule(
    Guid Id,
    string Name,
    bool Enabled,
    string Kind,
    // "HH:mm" in the server's local time - Kind "daily". Local rather than UTC deliberately:
    // "the porch light comes on at 18:00" means 18:00 on the wall clock, on both sides of a
    // daylight-saving change. ScheduleTiming is what makes that true.
    string TimeOfDay,
    // 0 = Sunday .. 6 = Saturday. Empty means every day. Applies to "daily" and "solar"; a cron
    // expression has its own day-of-week field and ignores this.
    IReadOnlyList<int> DaysOfWeek,
    int IntervalSeconds,
    // Optional "HH:mm" local bounds confining an interval schedule to part of the day, so
    // "every 15 minutes" doesn't also mean every 15 minutes at 3am. Empty means all day.
    // WindowStart later than WindowEnd is a window that crosses midnight (22:00 -> 06:00).
    string WindowStart,
    string WindowEnd,
    // Standard 5-field cron (minute hour day-of-month month day-of-week), parsed by Cronos.
    string CronExpression,
    // Kind "once" - the single absolute instant to fire at. Stored in UTC; the schedule disables
    // itself after it fires.
    DateTimeOffset? RunAtUtc,
    string SolarEvent,
    int SolarOffsetMinutes,
    // How far below the horizon the sun must be for a "dusk" or "dawn" firing - a darkness
    // level rather than a time. Ignored by "sunrise" and "sunset", which are defined at a fixed
    // elevation of their own. See SolarCalculator for what the numbers mean.
    //
    // This is the field that makes "close the coop when it gets dark, every night" work: the
    // clock time it resolves to moves by well over an hour across the year, all by itself,
    // because it is derived from where the sun actually is rather than from a number someone
    // has to remember to change in March and October.
    double SolarElevationDegrees,
    AutomationAction Action,
    // What to do about a firing time that passed while the server was down. See
    // ScheduleCatchUp.Decide.
    string CatchUpPolicy,
    DateTimeOffset? LastRunUtc,
    string LastStatus,
    string LastDetail,
    DateTimeOffset? NextDueUtc,
    long RunCount,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt)
{
    public const string KindDaily = "daily";
    public const string KindInterval = "interval";
    public const string KindCron = "cron";
    public const string KindOnce = "once";
    public const string KindSolar = "solar";

    public const string SolarSunrise = "sunrise";
    public const string SolarSunset = "sunset";

    // The two darkness-level events, evening and morning. Unlike sunrise/sunset they read
    // SolarElevationDegrees, so "dusk" can mean anything from the moment the sun touches the
    // horizon to full astronomical dark.
    public const string SolarDusk = "dusk";
    public const string SolarDawn = "dawn";

    // Default. A missed firing is logged and skipped - right for "flash the driveway light",
    // which is meaningless an hour late.
    public const string CatchUpSkip = "skip";

    // Fire once on the way back up, for schedules where the point is the state it leaves behind
    // ("arm at 22:00") rather than the moment it happens. Bounded by
    // ScheduleOptions.MaximumCatchUpMinutes so a machine that was off for days doesn't replay them.
    public const string CatchUpRunOnce = "runOnce";

    public static readonly IReadOnlyList<string> KnownKinds =
        [KindDaily, KindInterval, KindCron, KindOnce, KindSolar];

    public static readonly IReadOnlyList<string> KnownSolarEvents =
        [SolarSunrise, SolarSunset, SolarDusk, SolarDawn];

    public static bool IsEveningSolarEvent(string solarEvent) =>
        solarEvent?.ToLowerInvariant() is SolarSunset or SolarDusk;

    public static bool UsesElevation(string solarEvent) =>
        solarEvent?.ToLowerInvariant() is SolarDusk or SolarDawn;

    public static readonly IReadOnlyList<string> KnownCatchUpPolicies = [CatchUpSkip, CatchUpRunOnce];

    public static bool IsKnownKind(string kind) =>
        KnownKinds.Contains(kind, StringComparer.OrdinalIgnoreCase);

    // Only "solar" needs coordinates, and the settings that carry them are optional - so this is
    // what every layer that might have to explain "set your location first" tests against.
    public bool NeedsLocation => string.Equals(Kind, KindSolar, StringComparison.OrdinalIgnoreCase);
}

// The darkness levels offered as presets in every editor, so nobody has to know that "civil
// twilight" is -6 degrees to set up a coop door. Any value in SolarCalculator's supported range
// is still accepted - these are shortcuts, not the whole vocabulary.
public sealed record DarknessLevel(string Key, string Label, string Description, double ElevationDegrees)
{
    public static readonly IReadOnlyList<DarknessLevel> Presets =
    [
        new("sunset", "Sun on the horizon",
            "The moment the sun touches the horizon. Still broad daylight to look at.",
            SolarCalculator.SunriseElevationDegrees),
        new("civil", "Getting dark",
            "Civil twilight. Colours are fading and outdoor lights start going on - the usual choice for shutting a coop, since birds have gone in to roost by now.",
            SolarCalculator.CivilTwilightDegrees),
        new("nautical", "Properly dark",
            "Nautical twilight. The horizon is no longer distinct and it reads as night.",
            SolarCalculator.NauticalTwilightDegrees),
        new("astronomical", "Fully dark",
            "Astronomical twilight - the last of the sun's light is gone. At higher latitudes this is never reached at midsummer, and a schedule set to it will not fire on those nights.",
            SolarCalculator.AstronomicalTwilightDegrees)
    ];
}

// One firing, recorded whether it worked or not - the answer to "did my 18:00 light command
// actually go out?", which is the whole reason the history table exists.
public sealed record ScheduleRun(
    Guid Id,
    Guid ScheduleId,
    // When it was supposed to fire, versus when it did. The two differ by however long the
    // action queued behind others, and by the whole catch-up gap for a "missed" run.
    DateTimeOffset DueAtUtc,
    DateTimeOffset FiredAtUtc,
    string Status,
    string Detail,
    long DurationMs)
{
    public const string StatusOk = "ok";
    public const string StatusFailed = "failed";

    // The next firing came due while this one was still going. Recorded rather than dropped,
    // because a schedule that logs nothing but "skipped" is a schedule whose interval is too
    // short for its action, and that should be visible.
    public const string StatusSkipped = "skipped";

    // The firing time passed while the server was down or the clock jumped, and the catch-up
    // policy said not to run it late.
    public const string StatusMissed = "missed";
}
