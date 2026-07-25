namespace BlueIrisOllamaBridge.Web;

// Plain frame-diff motion detection, restricted to a zone polygon, sized by connected-component
// analysis - no OpenCV/AI, just byte-array math over a small grayscale grid. Good enough to catch
// "something object-sized moved inside this area"; not trying to classify what it was.
public sealed class MotionDetector
{
    public const int GridWidth = 320;
    public const int GridHeight = 180;
    public const int FrameSizeBytes = GridWidth * GridHeight;
    private const int FrameSize = FrameSizeBytes;
    private const int PixelDiffThreshold = 25;

    private readonly bool[] _zoneMask;
    private byte[]? _previousFrame;
    private readonly bool[] _changedScratch = new bool[FrameSize];
    private readonly bool[] _visitedScratch = new bool[FrameSize];
    private readonly int[] _stackScratch = new int[FrameSize];

    public MotionDetector(IReadOnlyList<TriggerPoint> zonePolygon)
    {
        _zoneMask = RasterizeZone(zonePolygon);
    }

    // Feeds one grayscale GridWidth*GridHeight frame. Returns the largest connected motion blob's
    // area (as a fraction of the frame) found inside the zone, or null if there's no prior frame yet.
    public double? ProcessFrame(byte[] frame)
    {
        if (frame.Length != FrameSize)
            throw new ArgumentException($"Expected a {FrameSize}-byte grayscale frame.", nameof(frame));

        if (_previousFrame is null)
        {
            _previousFrame = frame;
            return null;
        }

        Array.Clear(_changedScratch);
        for (var i = 0; i < FrameSize; i++)
        {
            if (!_zoneMask[i]) continue;
            var diff = Math.Abs(frame[i] - _previousFrame[i]);
            if (diff > PixelDiffThreshold) _changedScratch[i] = true;
        }

        _previousFrame = frame;

        var largestBlob = LargestConnectedComponent(_changedScratch);
        return (double)largestBlob / FrameSize;
    }

    private int LargestConnectedComponent(bool[] changed)
    {
        Array.Clear(_visitedScratch);
        var largest = 0;

        for (var start = 0; start < FrameSize; start++)
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
        var mask = new bool[FrameSize];
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
