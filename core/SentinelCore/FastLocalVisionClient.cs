using System.Text.Json;
using Microsoft.Extensions.Options;

namespace SentinelCore;

// The third vision backend, for hardware that can't run a VLM at usable speed: a small local
// object detector (see FastLocalDetector) whose detections are mapped deterministically into the
// same VisionAnalysis shape the VLM backends produce, so AlertProcessor, notification policy,
// and the UI need no idea which kind of backend ran.
//
// What it can honestly answer: person/vehicle/animal presence, vehicle kind (coarsely), and a
// confidence score - in well under a second on a CPU-only machine. What it can't: gate state,
// package detection, delivery companies, locations relative to the gate, or prose scene
// understanding - those fields come back "unknown"/absent, and FailoverVisionClient can
// optionally escalate to a VLM backend when this one finds a subject worth describing.
public sealed class FastLocalVisionClient(
    IFastLocalDetector detector,
    IOptionsMonitor<FastLocalOptions> options,
    ILogger<FastLocalVisionClient> logger) : IVisionAnalysisClient
{
    // The COCO-91 class ids this backend acts on. Everything else a COCO model can name
    // (furniture, appliances, food...) is background noise for a security camera and ignored.
    private const int Person = 1;

    private static readonly Dictionary<int, string> VehicleTypes = new()
    {
        // COCO's "truck" spans pickups, box trucks, and semis - "unknown" (can't tell which
        // type) is the honest value from a class label alone; "bus" and "bicycle" aren't in the
        // schema's type list at all, so they map to "other" with the word kept in Description.
        [2] = "other",   // bicycle
        [3] = "car",
        [4] = "motorcycle",
        [6] = "other",   // bus
        [8] = "unknown", // truck
    };

    private static readonly Dictionary<int, string> VehicleWords = new()
    {
        [2] = "bicycle", [3] = "car", [4] = "motorcycle", [6] = "bus", [8] = "truck",
    };

    private static readonly Dictionary<int, string> AnimalWords = new()
    {
        [16] = "bird", [17] = "cat", [18] = "dog", [19] = "horse",
        [20] = "sheep", [21] = "cow", [22] = "elephant", [23] = "bear",
    };

    public async Task<(VisionAnalysis Analysis, string Raw, string Model)> AnalyzeAsync(
        IncomingAlert alert, PromptProfile profile, CancellationToken cancellationToken)
    {
        var startedAt = DateTimeOffset.UtcNow;

        // Only the primary frame is analyzed, even when MultiFrameAnalysis supplied extras -
        // the extra frames exist for a VLM's temporal-consistency reasoning, which a
        // single-frame detector has no way to use.
        var detections = await detector.DetectAsync(alert.ImageBytes, profile.Crop, cancellationToken);

        // The camera's own category list (TriggerConfig.FastLocalClasses), when it has one: a
        // detection of anything else is treated as if it wasn't there, so it neither escalates to the
        // full model nor notifies. Raw below keeps every detection, so the event still shows what the
        // detector saw and chose to ignore.
        var analysis = MapToAnalysis(FastLocalCategories.Filter(detections, alert.FastLocalClasses), options.CurrentValue);
        var raw = JsonSerializer.Serialize(detections);
        var model = $"fastlocal/{detector.ModelName}";

        logger.LogInformation(
            "Fast local backend completed alert {AlertId} in {ElapsedMs:F0}ms: {Summary}",
            alert.Id,
            (DateTimeOffset.UtcNow - startedAt).TotalMilliseconds,
            analysis.Summary);

        return (analysis, raw, model);
    }

    public Task<bool> IsReadyAsync(CancellationToken cancellationToken) =>
        detector.IsReadyAsync(cancellationToken);

    // Pure and public for the same reason as OllamaVisionClient.BuildPrompt: this mapping is the
    // whole contract of the backend, so tests pin it directly without an ONNX session.
    public static VisionAnalysis MapToAnalysis(IReadOnlyList<FastLocalDetection> detections, FastLocalOptions options)
    {
        var people = detections.Where(d => d.ClassId == Person).ToList();
        var vehicles = detections.Where(d => VehicleTypes.ContainsKey(d.ClassId)).ToList();
        var animals = detections.Where(d => AnimalWords.ContainsKey(d.ClassId)).ToList();

        if (people.Count == 0 && vehicles.Count == 0 && animals.Count == 0)
        {
            return new VisionAnalysis
            {
                EventType = "none",
                Summary = "No person, vehicle, or animal detected.",
                // High, not low: "nothing above threshold" is a confident empty frame, and
                // nothing notifies off it anyway. Low confidence is reserved for the VLM's
                // "frame is unreadable" convention, which a detector can't distinguish.
                Confidence = 0.8,
                NotificationRecommended = false,
                NotificationReason = "no subject detected"
            };
        }

        var bestVehicle = vehicles.OrderByDescending(d => d.Score).FirstOrDefault();
        var summaryParts = new List<string>();
        if (people.Count > 0)
            summaryParts.Add(people.Count == 1 ? "a person" : $"{people.Count} people");
        if (bestVehicle is not null)
            summaryParts.Add(VehicleCountPhrase(vehicles, bestVehicle));
        if (animals.Count > 0)
            summaryParts.Add(AnimalPhrase(animals));

        var notify =
            (people.Count > 0 && options.NotifyOnPerson) ||
            (vehicles.Count > 0 && options.NotifyOnVehicle) ||
            (animals.Count > 0 && options.NotifyOnAnimal);

        var notifyReasons = new List<string>();
        if (people.Count > 0 && options.NotifyOnPerson) notifyReasons.Add("person");
        if (vehicles.Count > 0 && options.NotifyOnVehicle) notifyReasons.Add("vehicle");
        if (animals.Count > 0 && options.NotifyOnAnimal) notifyReasons.Add("animal");

        return new VisionAnalysis
        {
            // "animal" is the only allowed event_type that claims presence without claiming a
            // location. Person/vehicle event types all assert where the subject is relative to
            // the gate, which a class label can't know - "unknown" is the honest value, and the
            // summary carries what was actually seen.
            EventType = people.Count == 0 && vehicles.Count == 0 ? "animal" : "unknown",
            Summary = $"Fast detection: {JoinNatural(summaryParts)}.",
            Person = new PersonAnalysis
            {
                Visible = people.Count > 0,
                Location = people.Count > 0 ? "unknown" : "none",
                Activity = people.Count > 0 ? "unknown" : "none",
                Description = people.Count switch
                {
                    0 => "",
                    1 => "a person",
                    _ => $"{people.Count} people"
                }
            },
            Vehicle = new VehicleAnalysis
            {
                Visible = vehicles.Count > 0,
                Location = vehicles.Count > 0 ? "unknown" : "none",
                Activity = vehicles.Count > 0 ? "unknown" : "none",
                Type = bestVehicle is null ? "none" : VehicleTypes[bestVehicle.ClassId],
                Description = bestVehicle is null ? "" : VehicleCountPhrase(vehicles, bestVehicle)
            },
            Animal = new AnimalAnalysis
            {
                Visible = animals.Count > 0,
                Description = animals.Count > 0 ? AnimalPhrase(animals) : ""
            },
            Confidence = detections
                .Where(d => d.ClassId == Person || VehicleTypes.ContainsKey(d.ClassId) || AnimalWords.ContainsKey(d.ClassId))
                .Max(d => d.Score),
            NotificationRecommended = notify,
            NotificationReason = notify
                ? $"{JoinNatural(notifyReasons)} detected"
                : "detected subjects are not configured to notify"
        };
    }

    private static string VehicleCountPhrase(List<FastLocalDetection> vehicles, FastLocalDetection best)
    {
        var word = VehicleWords[best.ClassId];
        return vehicles.Count == 1 ? $"a {word}" : $"{vehicles.Count} vehicles ({word})";
    }

    private static string AnimalPhrase(List<FastLocalDetection> animals)
    {
        var words = animals
            .OrderByDescending(d => d.Score)
            .Select(d => AnimalWords[d.ClassId])
            .Distinct()
            .ToList();
        var joined = JoinNatural(words);
        return animals.Count == 1 ? $"an animal ({joined})" : $"{animals.Count} animals ({joined})";
    }

    private static string JoinNatural(List<string> parts) => parts.Count switch
    {
        0 => "",
        1 => parts[0],
        2 => $"{parts[0]} and {parts[1]}",
        _ => $"{string.Join(", ", parts[..^1])}, and {parts[^1]}"
    };
}

// The fast detector's categories as a camera chooses them - TriggerConfig.FastLocalClasses. The
// keys are the stable wire format every client sends and stores ("person", "truck", "bird"...), in
// the order the pickers list them; COCO class ids never leave this file.
//
// No list (null) means every category, which is what every camera did before this existed. An
// empty list is refused at the API rather than meaning "nothing": a camera whose detector can find
// nothing never escalates to the full model, so it would go silently blind.
public static class FastLocalCategories
{
    public static readonly IReadOnlyList<(string Key, int ClassId, string Group)> All =
    [
        ("person", 1, "person"),
        ("car", 3, "vehicle"),
        ("truck", 8, "vehicle"),
        ("motorcycle", 4, "vehicle"),
        ("bicycle", 2, "vehicle"),
        ("bus", 6, "vehicle"),
        ("dog", 18, "animal"),
        ("cat", 17, "animal"),
        ("bird", 16, "animal"),
        ("horse", 19, "animal"),
        ("cow", 21, "animal"),
        ("sheep", 20, "animal"),
        ("bear", 23, "animal"),
        ("elephant", 22, "animal"),
    ];

    public static IReadOnlyList<string> Keys { get; } = All.Select(c => c.Key).ToList();

    // Known keys only, lower-cased, de-duplicated and in the pickers' order - so two clients that
    // tick the same boxes in a different order store the same thing. Null in, null out.
    public static IReadOnlyList<string>? Normalize(IEnumerable<string>? keys)
    {
        if (keys is null) return null;
        var wanted = keys.Select(k => k.Trim().ToLowerInvariant()).ToHashSet();
        return Keys.Where(wanted.Contains).ToList();
    }

    public static IReadOnlyList<string> Unknown(IEnumerable<string> keys) =>
        keys.Select(k => k.Trim().ToLowerInvariant()).Where(k => !Keys.Contains(k)).Distinct().ToList();

    public static IReadOnlyList<FastLocalDetection> Filter(
        IReadOnlyList<FastLocalDetection> detections, IReadOnlyList<string>? keys)
    {
        if (keys is null) return detections;
        var ids = All.Where(c => keys.Contains(c.Key)).Select(c => c.ClassId).ToHashSet();
        return detections.Where(d => ids.Contains(d.ClassId)).ToList();
    }
}
