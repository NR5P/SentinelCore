namespace SentinelCore.Web;

// Frame-diff motion detection, restricted to a zone polygon, sized by connected-component
// analysis - no OpenCV/AI, just byte-array math over a small RGB grid. A changed pixel is
// excluded from the diff if it looks like a shadow rather than a real object: shadows darken
// a pixel while keeping its color ratios close to the background (grass in shadow still reads
// as green, just dimmer), while a real object's color isn't a scaled-down version of what was
// there before. This is the standard classical-CV shadow suppression technique (the same idea
// behind OpenCV's MOG2 shadow detector), adapted to a simple two-frame diff instead of a
// learned background model.
public sealed class MotionDetector
{
    public const int GridWidth = 320;
    public const int GridHeight = 180;
    public const int PixelCount = GridWidth * GridHeight;
    public const int FrameSizeBytes = PixelCount * 3; // RGB24

    private const int LumaDiffThreshold = 25;

    // A pixel counts as "shadow" only if it got moderately darker (not near-black, not brighter)
    // while every channel dimmed by roughly the same proportion.
    private const double ShadowAlphaMin = 0.20;
    private const double ShadowAlphaMax = 0.95;
    private const double DarkChannelFloor = 8.0;

    // ShadowSensitivity (0-100, user-facing) controls how forgiving the "same proportion across
    // channels" check is. 0 disables shadow filtering entirely (matches pre-shadow-suppression
    // behavior); higher values tolerate more chromaticity drift before ruling a pixel "not a
    // shadow", i.e. filter more aggressively - useful for cameras with harsher/noisier shadows,
    // at the cost of occasionally excluding a real object that happens to keep the background hue.
    private const double MinChromaticityThreshold = 0.04;
    private const double MaxChromaticityThreshold = 0.24;

    private readonly bool[] _zoneMask;
    private readonly double _shadowChromaticityThreshold;
    private byte[]? _previousFrame;
    private readonly bool[] _changedScratch = new bool[PixelCount];
    private readonly bool[] _visitedScratch = new bool[PixelCount];
    private readonly int[] _stackScratch = new int[PixelCount];

    public MotionDetector(IReadOnlyList<TriggerPoint> zonePolygon, int shadowSensitivity = 50)
    {
        _zoneMask = RasterizeZone(zonePolygon);
        _shadowChromaticityThreshold = shadowSensitivity <= 0
            ? -1 // sentinel: never matches, so no pixel is ever excluded as shadow
            : MinChromaticityThreshold + Math.Clamp(shadowSensitivity, 1, 100) / 100.0 * (MaxChromaticityThreshold - MinChromaticityThreshold);
    }

    // Feeds one RGB24 GridWidth*GridHeight frame. Returns the largest connected motion blob's
    // area (as a fraction of the frame) found inside the zone, or null if there's no prior frame yet.
    public double? ProcessFrame(byte[] frame)
    {
        if (frame.Length != FrameSizeBytes)
            throw new ArgumentException($"Expected a {FrameSizeBytes}-byte RGB24 frame.", nameof(frame));

        if (_previousFrame is null)
        {
            _previousFrame = frame;
            return null;
        }

        Array.Clear(_changedScratch);
        for (var i = 0; i < PixelCount; i++)
        {
            if (!_zoneMask[i]) continue;

            var o = i * 3;
            byte r = frame[o], g = frame[o + 1], b = frame[o + 2];
            byte r0 = _previousFrame[o], g0 = _previousFrame[o + 1], b0 = _previousFrame[o + 2];

            var lumaDiff = Math.Abs(Luma(r, g, b) - Luma(r0, g0, b0));
            if (lumaDiff <= LumaDiffThreshold) continue;
            if (IsLikelyShadow(r, g, b, r0, g0, b0)) continue;

            _changedScratch[i] = true;
        }

        _previousFrame = frame;

        var largestBlob = LargestConnectedComponent(_changedScratch);
        return (double)largestBlob / PixelCount;
    }

    private static double Luma(byte r, byte g, byte b) => 0.299 * r + 0.587 * g + 0.114 * b;

    private bool IsLikelyShadow(byte r, byte g, byte b, byte r0, byte g0, byte b0)
    {
        // Can't judge a color ratio against a near-black background pixel - treat as a real change.
        if (r0 < DarkChannelFloor || g0 < DarkChannelFloor || b0 < DarkChannelFloor) return false;

        var alpha = Luma(r, g, b) / Math.Max(1.0, Luma(r0, g0, b0));
        if (alpha < ShadowAlphaMin || alpha > ShadowAlphaMax) return false;

        var dr = r / (double)r0 - alpha;
        var dg = g / (double)g0 - alpha;
        var db = b / (double)b0 - alpha;
        var chromaticityDistortion = Math.Sqrt((dr * dr + dg * dg + db * db) / 3.0);

        return chromaticityDistortion < _shadowChromaticityThreshold;
    }

    private int LargestConnectedComponent(bool[] changed)
    {
        Array.Clear(_visitedScratch);
        var largest = 0;

        for (var start = 0; start < PixelCount; start++)
        {
            if (!changed[start] || _visitedScratch[start]) continue;

            var stackTop = 0;
            _stackScratch[stackTop++] = start;
            _visitedScratch[start] = true;
            var size = 0;

            while (stackTop > 0)
            {
                var index = _stackScratch[--stackTop];
                size++;

                var x = index % GridWidth;
                var y = index / GridWidth;

                TryPush(changed, x - 1, y, ref stackTop);
                TryPush(changed, x + 1, y, ref stackTop);
                TryPush(changed, x, y - 1, ref stackTop);
                TryPush(changed, x, y + 1, ref stackTop);
            }

            if (size > largest) largest = size;
        }

        return largest;
    }

    private void TryPush(bool[] changed, int x, int y, ref int stackTop)
    {
        if (x < 0 || x >= GridWidth || y < 0 || y >= GridHeight) return;
        var index = y * GridWidth + x;
        if (!changed[index] || _visitedScratch[index]) return;
        _visitedScratch[index] = true;
        _stackScratch[stackTop++] = index;
    }

    private static bool[] RasterizeZone(IReadOnlyList<TriggerPoint> polygon)
    {
        var mask = new bool[PixelCount];
        if (polygon.Count < 3) return mask;

        for (var y = 0; y < GridHeight; y++)
        {
            var py = (y + 0.5) / GridHeight;
            for (var x = 0; x < GridWidth; x++)
            {
                var px = (x + 0.5) / GridWidth;
                if (IsInPolygon(polygon, px, py)) mask[y * GridWidth + x] = true;
            }
        }

        return mask;
    }

    // Standard ray-casting point-in-polygon test.
    private static bool IsInPolygon(IReadOnlyList<TriggerPoint> polygon, double px, double py)
    {
        var inside = false;
        for (int i = 0, j = polygon.Count - 1; i < polygon.Count; j = i++)
        {
            var a = polygon[i];
            var b = polygon[j];
            var intersects = (a.Y > py) != (b.Y > py) &&
                px < (b.X - a.X) * (py - a.Y) / (b.Y - a.Y) + a.X;
            if (intersects) inside = !inside;
        }

        return inside;
    }
}
