using System.Globalization;

namespace SentinelCore.Web;

// Following a subject with the PTZ camera and then putting it back.
//
// Off by default, and deliberately so: it moves a camera on its own, and a security camera that
// moves for reasons the owner didn't ask for is worse than one that doesn't.
//
// The camera has tracking of its own (Reolink's aiTrack), which is smoother than this can be - it
// runs on the sensor with no network in the loop. This exists because that one is invisible to the
// server: it pans without telling anyone, so trigger zones carry on measuring a scene that has
// moved (see PtzActivityTracker). Driving the camera from here is slower but coherent - detection,
// homing and clips all stay in agreement about where the camera is pointing.
public sealed class PtzTrackingOptions
{
    public const string SectionName = "PtzTracking";

    public bool Enabled { get; init; }

    // What to follow, as COCO class names or raw class ids - see PtzTrackClasses. Names because
    // ids are unreadable; ids accepted too, because this model knows classes there is no name for
    // here and being unable to name one shouldn't make it untrackable.
    public string[] Track { get; init; } = ["person"];

    // Below this the detection is treated as nothing at all. Tracking acts on single frames with no
    // temporal smoothing, so a marginal detection is a camera that swings at a shadow.
    public double MinimumScore { get; init; } = 0.5;

    // How far off-centre the subject may be, as a fraction of the frame, before the camera moves.
    // This is the single most important number here. The control loop is slow - a frame, then
    // inference, then a command over the network - so a tight deadzone makes the camera hunt back
    // and forth around the subject rather than settle.
    public double Deadzone { get; init; } = 0.12;

    // ONVIF velocity for a correction, at the point where the subject is at the frame edge; the
    // actual velocity scales with how far off-centre it is, so small errors get small moves.
    public double Speed { get; init; } = 0.35;

    // How long each correction runs before stopping. Bursts rather than continuous movement,
    // because the loop only learns where the subject went on the next frame - moving until then
    // would overshoot by however long the loop takes.
    public int NudgeMilliseconds { get; init; } = 350;

    public int PollMilliseconds { get; init; } = 500;

    // No trackable subject for this long and the camera goes home. Long enough to ride out a
    // person stepping behind something, short enough not to sit pointed at nothing.
    public int LostSeconds { get; init; } = 8;

    // How long after a track ends before another may begin on the same camera.
    //
    // Without this the camera ping-pongs: a track ends, it homes, detection resumes, the very next
    // trigger starts another track, and the camera spends its life away from home with its zones
    // suppressed. Seen immediately the first time this ran against a real camera - the settling
    // motion of arriving home was itself enough to fire the zone that started the next track.
    // The rest period is also what guarantees the zones get uninterrupted coverage between tracks,
    // which is the job they were there to do in the first place.
    public int RestSeconds { get; init; } = 30;

    // A hard stop, however well tracking seems to be going. Without it a swaying branch that the
    // model reads as a subject walks the camera off its home position indefinitely, and the first
    // anyone knows is that the zones stopped firing. Must stay comfortably below
    // PtzHomingOptions.IdleMinutes so the two never race for the same camera.
    public int MaxTrackSeconds { get; init; } = 120;
}

// COCO class names for the detector's class ids, so tracking can be configured in words.
//
// The id space is COCO's original 91-entry one, 1-indexed and with gaps - the same one
// FastLocalVisionClient's person/vehicle/animal tables index into, so the numbers here agree with
// the ones already in use rather than introducing a second convention.
public static class PtzTrackClasses
{
    private static readonly Dictionary<string, int> ByName = new(StringComparer.OrdinalIgnoreCase)
    {
        ["person"] = 1, ["bicycle"] = 2, ["car"] = 3, ["motorcycle"] = 4, ["airplane"] = 5,
        ["bus"] = 6, ["train"] = 7, ["truck"] = 8, ["boat"] = 9, ["traffic light"] = 10,
        ["fire hydrant"] = 11, ["stop sign"] = 13, ["parking meter"] = 14, ["bench"] = 15,
        ["bird"] = 16, ["cat"] = 17, ["dog"] = 18, ["horse"] = 19, ["sheep"] = 20, ["cow"] = 21,
        ["elephant"] = 22, ["bear"] = 23, ["zebra"] = 24, ["giraffe"] = 25, ["backpack"] = 27,
        ["umbrella"] = 28, ["handbag"] = 31, ["tie"] = 32, ["suitcase"] = 33, ["frisbee"] = 34,
        ["skis"] = 35, ["snowboard"] = 36, ["sports ball"] = 37, ["kite"] = 38,
        ["baseball bat"] = 39, ["baseball glove"] = 40, ["skateboard"] = 41, ["surfboard"] = 42,
        ["tennis racket"] = 43, ["bottle"] = 44, ["wine glass"] = 46, ["cup"] = 47, ["fork"] = 48,
        ["knife"] = 49, ["spoon"] = 50, ["bowl"] = 51, ["banana"] = 52, ["apple"] = 53,
        ["sandwich"] = 54, ["orange"] = 55, ["broccoli"] = 56, ["carrot"] = 57, ["hot dog"] = 58,
        ["pizza"] = 59, ["donut"] = 60, ["cake"] = 61, ["chair"] = 62, ["couch"] = 63,
        ["potted plant"] = 64, ["bed"] = 65, ["dining table"] = 67, ["toilet"] = 70, ["tv"] = 72,
        ["laptop"] = 73, ["mouse"] = 74, ["remote"] = 75, ["keyboard"] = 76, ["cell phone"] = 77,
        ["microwave"] = 78, ["oven"] = 79, ["toaster"] = 80, ["sink"] = 81, ["refrigerator"] = 82,
        ["book"] = 84, ["clock"] = 85, ["vase"] = 86, ["scissors"] = 87, ["teddy bear"] = 88,
        ["hair drier"] = 89, ["toothbrush"] = 90
    };

    // Unrecognised entries are dropped rather than failing the whole configuration: a typo in one
    // class name should cost that class, not the feature.
    public static (HashSet<int> Ids, List<string> Unrecognised) Resolve(IEnumerable<string> names)
    {
        var ids = new HashSet<int>();
        var unrecognised = new List<string>();

        foreach (var raw in names)
        {
            var name = raw?.Trim() ?? string.Empty;
            if (name.Length == 0) continue;

            if (ByName.TryGetValue(name, out var id)) { ids.Add(id); continue; }
            // A bare number, for a class this table has no name for.
            if (int.TryParse(name, NumberStyles.Integer, CultureInfo.InvariantCulture, out var numeric) && numeric > 0)
            {
                ids.Add(numeric);
                continue;
            }

            unrecognised.Add(name);
        }

        return (ids, unrecognised);
    }

    public static string Describe(int classId) =>
        ByName.FirstOrDefault(pair => pair.Value == classId).Key ?? $"class {classId}";
}
