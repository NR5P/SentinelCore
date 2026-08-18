using FaceAiSharp;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;

namespace SentinelCore;

// Face detection with 5-point landmarks, run locally through ONNX Runtime. This replaces
// FaceAiSharp's bundled ScrfdDetector, which was not a technical decision: SCRFD's weights come
// from InsightFace under "non-commercial research purposes only", so shipping them inside a paid
// release was not something a licence key could fix. YuNet (github.com/opencv/opencv_zoo,
// models/face_detection_yunet) is MIT, 227KB against SCRFD's several megabytes, and gives up about
// 3-5 points of WIDER FACE AP - a gap the ArcFace stage largely absorbs anyway, because a face too
// small for YuNet to find is also too small to embed into anything that clears KnownFaceMatcher's
// cosine threshold.
//
// Implemented by hand rather than through OpenCvSharp's FaceDetectorYN, which would have dragged
// tens of megabytes of native OpenCV binaries into every platform's release for exactly one model.
// The decode below follows OpenCV's own modules/objdetect/src/face_detect.cpp.
public sealed class YuNetFaceDetector : IFaceDetectorWithLandmarks, IDisposable
{
    // This export has a static 1x3x640x640 input, confirmed from its own metadata, and output rows
    // fixed to match (6400/1600/400 for strides 8/16/32). OpenCV's FaceDetectorYN appears to
    // accept any size only because OpenCV's dnn engine re-infers shapes over the graph; ONNX
    // Runtime does not, and hands back a shape error instead. So every frame is letterboxed to
    // exactly 640x640 rather than padded at its native resolution.
    //
    // The practical consequence worth knowing: YuNet finds faces down to roughly 10px *in that
    // 640-wide frame*. On a 4K camera that is about a 64px face in the original - fine for a
    // doorbell or porch, marginal for someone at the far end of a driveway. If distant faces
    // matter later, the fix is to detect on a crop around the motion region rather than to raise
    // this number, which the model's fixed input will not allow anyway.
    private const int InputSize = 640;

    // YuNet is fully convolutional over three strides; each feature cell holds one anchor.
    private static readonly int[] Strides = [8, 16, 32];

    // OpenCV's own defaults for this model.
    private const float ScoreThreshold = 0.9f;
    private const float NmsThreshold = 0.3f;

    private readonly InferenceSession _session;
    private readonly string _inputName;

    public YuNetFaceDetector(string modelPath)
    {
        _session = new InferenceSession(modelPath);
        _inputName = _session.InputMetadata.Keys.First();
    }

    public IReadOnlyCollection<FaceDetectorResult> DetectFaces(Image<Rgb24> image)
    {
        ArgumentNullException.ThrowIfNull(image);

        // Uniform scale so the frame fits inside the square, then composite at the origin. Pinning
        // to (0,0) rather than centring means the padding costs nothing to unwind: a coordinate
        // decoded in the 640x640 frame only has to be divided by this scale to be correct in the
        // original.
        var scale = Math.Min((double)InputSize / image.Width, (double)InputSize / image.Height);
        var scaledWidth = Math.Clamp((int)Math.Round(image.Width * scale), 1, InputSize);
        var scaledHeight = Math.Clamp((int)Math.Round(image.Height * scale), 1, InputSize);

        using var scaled = image.Clone(ctx => ctx.Resize(scaledWidth, scaledHeight));
        using var prepared = new Image<Rgb24>(InputSize, InputSize, new Rgb24(0, 0, 0));
        prepared.Mutate(ctx => ctx.DrawImage(scaled, new Point(0, 0), 1f));

        using var results = _session.Run([NamedOnnxValue.CreateFromTensor(_inputName, ToTensor(prepared))]);
        var outputs = results.ToDictionary(r => r.Name, r => r.AsTensor<float>().ToArray(), StringComparer.Ordinal);

        var candidates = new List<FaceDetectorResult>();
        foreach (var stride in Strides)
        {
            Decode(outputs, stride, scale, candidates);
        }

        return NonMaxSuppression(candidates);
    }

    // The landmark contract, which is the whole reason this detector can stand in for SCRFD.
    // OpenCV documents YuNet's five keypoints as right eye, left eye, nose, right mouth, left
    // mouth, while ArcFaceEmbeddingsGenerator.AlignFaceUsingLandmarks documents its input as left
    // eye, right eye, nose, left mouth, right mouth - which reads like the first two and last two
    // need swapping. They do not. OpenCV's own alignCrop feeds these landmarks in emitted order
    // onto the reference template {38.29,51.70}, {73.53,51.50}, {56.03,71.74}, {41.55,92.37},
    // {70.73,92.20} - the standard ArcFace 112x112 template, the same one FaceAiSharp aligns to.
    // Index 0 lands at x=38 on a 112-wide chip, i.e. the left of the image. So OpenCV's "right
    // eye" is the subject's right eye, which appears on the image's left, and is the same physical
    // point InsightFace calls the "left eye". The two orderings are identical; swapping them would
    // mirror every face and quietly ruin every embedding while still detecting faces perfectly.
    private static void Decode(
        IReadOnlyDictionary<string, float[]> outputs,
        int stride,
        double scale,
        List<FaceDetectorResult> into)
    {
        var cls = outputs[$"cls_{stride}"];
        var obj = outputs[$"obj_{stride}"];
        var bbox = outputs[$"bbox_{stride}"];
        var kps = outputs[$"kps_{stride}"];

        var cols = InputSize / stride;
        var rows = InputSize / stride;

        for (var r = 0; r < rows; r++)
        {
            for (var c = 0; c < cols; c++)
            {
                var idx = (r * cols) + c;
                if (idx >= cls.Length || idx >= obj.Length) continue;

                // Both heads are already sigmoid outputs; the clamp guards against the small
                // excursions past 0/1 that quantised graphs can produce, which would otherwise
                // make the square root NaN.
                var clsScore = Math.Clamp(cls[idx], 0f, 1f);
                var objScore = Math.Clamp(obj[idx], 0f, 1f);
                var score = MathF.Sqrt(clsScore * objScore);
                if (score < ScoreThreshold) continue;

                var centerX = (c + bbox[(idx * 4) + 0]) * stride;
                var centerY = (r + bbox[(idx * 4) + 1]) * stride;
                var width = MathF.Exp(bbox[(idx * 4) + 2]) * stride;
                var height = MathF.Exp(bbox[(idx * 4) + 3]) * stride;

                var box = new RectangleF(
                    (float)((centerX - (width / 2)) / scale),
                    (float)((centerY - (height / 2)) / scale),
                    (float)(width / scale),
                    (float)(height / scale));

                var landmarks = new PointF[5];
                for (var n = 0; n < 5; n++)
                {
                    landmarks[n] = new PointF(
                        (float)((kps[(idx * 10) + (2 * n)] + c) * stride / scale),
                        (float)((kps[(idx * 10) + (2 * n) + 1] + r) * stride / scale));
                }

                into.Add(new FaceDetectorResult(box, landmarks, score));
            }
        }
    }

    // Greedy IoU suppression, highest score first - the three strides overlap heavily on the same
    // face, so this is not optional.
    private static List<FaceDetectorResult> NonMaxSuppression(List<FaceDetectorResult> candidates)
    {
        var ordered = candidates.OrderByDescending(d => d.Confidence ?? 0f).ToList();
        var kept = new List<FaceDetectorResult>();

        foreach (var candidate in ordered)
        {
            var suppressed = false;
            foreach (var k in kept)
            {
                if (IntersectionOverUnion(candidate.Box, k.Box) > NmsThreshold)
                {
                    suppressed = true;
                    break;
                }
            }

            if (!suppressed) kept.Add(candidate);
        }

        return kept;
    }

    private static float IntersectionOverUnion(RectangleF a, RectangleF b)
    {
        var intersection = RectangleF.Intersect(a, b);
        if (intersection.IsEmpty) return 0f;

        var intersectionArea = intersection.Width * intersection.Height;
        var union = (a.Width * a.Height) + (b.Width * b.Height) - intersectionArea;
        return union <= 0f ? 0f : intersectionArea / union;
    }

    // YuNet was trained on OpenCV's blobFromImage output: raw 0-255 channel values in BGR order,
    // with no mean subtraction and no scaling. Feeding RGB here detects faces almost as well and
    // then places the landmarks slightly wrong, which is the kind of bug that shows up only as
    // mediocre recognition accuracy - hence the explicit channel order below.
    private static DenseTensor<float> ToTensor(Image<Rgb24> image)
    {
        var tensor = new DenseTensor<float>([1, 3, InputSize, InputSize]);
        image.ProcessPixelRows(accessor =>
        {
            for (var y = 0; y < accessor.Height; y++)
            {
                var row = accessor.GetRowSpan(y);
                for (var x = 0; x < row.Length; x++)
                {
                    tensor[0, 0, y, x] = row[x].B;
                    tensor[0, 1, y, x] = row[x].G;
                    tensor[0, 2, y, x] = row[x].R;
                }
            }
        });
        return tensor;
    }

    public IReadOnlyList<PointF> DetectLandmarks(Image<Rgb24> image)
    {
        var faces = DetectFaces(image);
        return faces.Count == 0 ? [] : faces.First().Landmarks ?? [];
    }

    // Matching ScrfdDetector's convention, and consistent with the ordering note on Decode:
    // index 0 is the eye on the image's left, index 1 the eye on its right.
    public PointF GetLeftEyeCenter(IReadOnlyList<PointF> landmarks)
    {
        ArgumentNullException.ThrowIfNull(landmarks);
        return landmarks[0];
    }

    public PointF GetRightEyeCenter(IReadOnlyList<PointF> landmarks)
    {
        ArgumentNullException.ThrowIfNull(landmarks);
        return landmarks[1];
    }

    public void Dispose() => _session.Dispose();
}
