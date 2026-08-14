namespace SentinelCore.Web;

// A per-camera window during which motion triggers are held back entirely.
//
// This exists because of what the gate camera does after dark. Measured over three days in August
// 2026: between 21:00 and 07:00 it produced 284 triggers of which 5 survived AI confirmation - 21%
// of the day's motion for 2% of its real events. Daylight over the same period ran 23% confirmed.
// The night flood is the IR illuminator lighting up whatever sits close to the lens (a spider web,
// dust, condensation), and the blobs it makes are the same apparent SIZE as a person at the gate,
// so no minimum-object-size setting can separate them - raising the floor far enough to lose the
// web loses the people too. Time of day is the one axis on which the two actually differ.
//
// Deliberately NOT the same thing as the main app's QuietHours, which raises the confidence bar a
// notification has to clear. This suppresses the trigger before there is anything to notify about:
// no snapshot, no clip, no alert, and - the point of the exercise - no VLM analysis, which is what
// the wasted GPU time was being spent on.
public static class TriggerNightWindow
{
    // Local time, matching QuietHoursPolicy. Both the detector and the person setting the hours
    // are looking at the same wall clock, and a window expressed in UTC would have to be re-derived
    // every daylight-saving change to keep meaning "after dark".
    //
    // Note this is the HOST's clock, not the camera's - the gate camera's own OSD clock is
    // currently three hours off, and nothing here reads it.
    public static bool IsActive(TriggerConfig config, DateTimeOffset at)
    {
        if (!config.NightWindowEnabled) return false;

        // Start == End is a zero-width window (never active) rather than a full day. An untouched
        // 0/0 therefore suppresses nothing, which is the safe reading of "someone enabled this
        // without setting hours" - the opposite default would blind the camera around the clock.
        if (config.NightStartHour == config.NightEndHour) return false;

        var hour = at.ToLocalTime().Hour;
        return config.NightStartHour < config.NightEndHour
            ? hour >= config.NightStartHour && hour < config.NightEndHour
            : hour >= config.NightStartHour || hour < config.NightEndHour;
    }

    // When the current window ends, for the log line that explains the silence. Only meaningful
    // while IsActive; returns the next occurrence of EndHour on the host clock.
    public static DateTimeOffset EndsAt(TriggerConfig config, DateTimeOffset at)
    {
        var local = at.ToLocalTime();
        var end = new DateTimeOffset(local.Year, local.Month, local.Day, config.NightEndHour, 0, 0, local.Offset);
        return end <= local ? end.AddDays(1) : end;
    }
}
