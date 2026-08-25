extern alias WebHost;

using System.Text.RegularExpressions;
using Xunit;
using MotionDetector = WebHost::SentinelCore.Web.MotionDetector;

namespace SentinelCore.Tests;

// The motion detector runs on a fixed grid, and a zone's MinTriggerArea is stored as a fraction
// of the frame. To show that fraction as the "detector pixels" number people actually set, every
// client multiplies it by the grid's area - and every client does that with its own hard-coded
// copy of the grid size, because there is nowhere in the API that carries it.
//
// Nothing enforces those copies. If GridHeight ever changed, the server would start detecting
// against a different grid while all three clients carried on drawing the old scale: the slider
// range, the number under it and the tooltip would every one of them be wrong, and nothing would
// fail. Someone sizing "a person at the gate" would be sizing it against a frame that no longer
// exists.
//
// This is the same drift SettingsSchema exists to prevent, in the one place that schema doesn't
// reach. Reading the client sources is crude, but the alternative is finding out from a user
// whose trigger zones quietly stopped matching what they set.
public sealed class DetectorGridConstantTests
{
    // Walked up from the test binary rather than hard-coded, so this keeps working wherever the
    // checkout lives. Null when the sources aren't alongside (a source-only or packaged build),
    // which skips rather than fails.
    private static readonly string? RepositoryRoot = FindRepositoryRoot();

    private static string? FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (Directory.Exists(Path.Combine(directory.FullName, "core")) &&
                Directory.Exists(Path.Combine(directory.FullName, "ios")) &&
                Directory.Exists(Path.Combine(directory.FullName, "android")))
            {
                return directory.FullName;
            }
            directory = directory.Parent;
        }
        return null;
    }

    public static TheoryData<string, string> ClientCopies() => new()
    {
        { "core/SentinelCore.Web/wwwroot/triggers.js", @"DETECTOR_PIXELS\s*=\s*(\d+)\s*\*\s*(\d+)" },
        { "ios/SentinelCore/Views/TriggerZoneEditorView.swift", @"detectorPixels\s*=\s*(\d+)(?:\.0)?\s*\*\s*(\d+)(?:\.0)?" },
        { "android/app/src/main/java/com/sentinelcore/android/ui/screens/CameraZoneEditorScreen.kt",
          @"DETECTOR_PIXELS\s*=\s*(\d+)(?:\.0)?\s*\*\s*(\d+)(?:\.0)?" },
    };

    [Theory]
    [MemberData(nameof(ClientCopies))]
    public void EveryClientsDetectorGrid_MatchesTheOneTheServerActuallyDetectsOn(string relativePath, string pattern)
    {
        Assert.SkipWhen(RepositoryRoot is null, "The client sources aren't alongside this build.");

        var path = Path.Combine(RepositoryRoot!, relativePath);

        // Running somewhere without the full checkout (a packaging job, a source-only build) is
        // not a failure of the invariant - there is simply nothing to compare against.
        Assert.SkipUnless(File.Exists(path), $"{relativePath} is not present in this checkout.");

        var match = Regex.Match(File.ReadAllText(path), pattern);
        Assert.True(match.Success,
            $"{relativePath} no longer declares its detector grid in a form this test recognises. " +
            "If the constant moved, point this test at wherever it went - don't delete the check.");

        var width = int.Parse(match.Groups[1].Value);
        var height = int.Parse(match.Groups[2].Value);

        Assert.True(
            width == MotionDetector.GridWidth && height == MotionDetector.GridHeight,
            $"{relativePath} sizes the detector grid at {width}x{height}, but MotionDetector runs at " +
            $"{MotionDetector.GridWidth}x{MotionDetector.GridHeight}. Every object-size number that client " +
            "shows is scaled by this, so the two disagreeing means the slider, the pixel count and the " +
            "tooltip are all describing a frame the detector isn't using.");
    }
}
