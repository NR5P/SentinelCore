using FaceAiSharp;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;

namespace SentinelCore;

public interface IFaceRecognitionService
{
    IReadOnlyList<float[]> DetectFaceEmbeddings(byte[] imageBytes);
}

// Detects faces in an alert image and turns each one into an embedding vector, entirely locally
// via ONNX Runtime (YuNet detector + ArcFace embedding model) - no network call,
// independent of whichever vision backend (Ollama or cloud) is analyzing the same alert
// for its descriptive summary. Registered as a singleton: both models hold an ONNX Runtime
// session that's expensive to create and safe to reuse, so they're loaded once for the process
// lifetime, the same way Ollama's model is kept loaded between alerts.
public sealed class FaceRecognitionService : IFaceRecognitionService, IDisposable
{
    // Deliberately not FaceAiSharpBundleFactory, which is otherwise exactly what this wants.
    // That factory locates its bundled .onnx files relative to Assembly.Location, and in a
    // single-file publish - which is how every release .zip is built, see
    // scripts/publish-release.sh - Assembly.Location is an empty string, because the assemblies
    // live inside the executable rather than on disk. Path.GetDirectoryName("") returns null, so
    // constructing this threw ArgumentNullException and took the whole process down before it
    // served a single request. AppContext.BaseDirectory is the executable's own directory in both
    // layouts, and the models are published next to it either way.
    //
    // YuNet rather than SCRFD: see YuNetFaceDetector for why, but the short version is that
    // SCRFD's weights are licensed for non-commercial research only and cannot ship in a paid
    // release. This file is MIT, from github.com/opencv/opencv_zoo.
    private const string DetectorModel = "face_detection_yunet_2023mar.onnx";
    private const string EmbeddingModel = "arcfaceresnet100-11-int8.onnx";

    // Lazy for the reason this bug was so damaging rather than merely annoying: these are
    // registered as an eagerly-resolved singleton, so anything the constructor threw was fatal to
    // a process whose actual job is watching cameras. Face recognition is opt-in and purely
    // additive, so a model that won't load should cost exactly that feature - the failure now
    // surfaces on first use, where DetectFaceEmbeddings already logs and degrades to no faces.
    private readonly Lazy<IFaceDetectorWithLandmarks> _detector;
    private readonly Lazy<IFaceEmbeddingsGenerator> _embedder;
    private readonly ILogger<FaceRecognitionService> _logger;

    public FaceRecognitionService(ILogger<FaceRecognitionService> logger)
    {
        _logger = logger;
        _detector = new Lazy<IFaceDetectorWithLandmarks>(() =>
            new YuNetFaceDetector(ModelPath(DetectorModel)));
        _embedder = new Lazy<IFaceEmbeddingsGenerator>(() =>
            new ArcFaceEmbeddingsGenerator(new ArcFaceEmbeddingsGeneratorOptions { ModelPath = ModelPath(EmbeddingModel) }));
    }

    // Names the file and where it was expected. The models ship as NuGet content files copied to
    // onnx/ in the build output, so an absent one means a broken publish rather than anything the
    // user did - and that is worth saying plainly in the log instead of an ONNX Runtime error
    // about a path nobody chose.
    private static string ModelPath(string fileName)
    {
        var path = Path.Combine(AppContext.BaseDirectory, "onnx", fileName);
        if (!File.Exists(path))
        {
            throw new FileNotFoundException(
                $"Face recognition model '{fileName}' is missing from this install. Expected it at {path}.", path);
        }
        return path;
    }

    public IReadOnlyList<float[]> DetectFaceEmbeddings(byte[] imageBytes)
    {
        try
        {
            using var image = Image.Load<Rgb24>(SafeImageDecoding.Options, imageBytes);
            var faces = _detector.Value.DetectFaces(image);
            if (faces.Count == 0) return [];

            var embeddings = new List<float[]>(faces.Count);
            foreach (var face in faces)
            {
                if (face.Landmarks is not { Count: > 0 } landmarks) continue;

                // AlignFaceUsingLandmarks crops/warps its image argument in place into a
                // normalized face chip, so each face needs its own clone of the original frame -
                // reusing one image across multiple faces would align the second face into an
                // image already cropped down to the first.
                using var faceImage = image.Clone();
                _embedder.Value.AlignFaceUsingLandmarks(faceImage, landmarks);
                embeddings.Add(_embedder.Value.GenerateEmbedding(faceImage));
            }

            return embeddings;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Face recognition is opt-in and purely additive to an alert's analysis - a
            // decode/inference failure here shouldn't take down the rest of the alert.
            _logger.LogWarning(ex, "Face detection failed for an alert image");
            return [];
        }
    }

    // The detector holds an ONNX Runtime session. Only dispose what was actually built - touching
    // .Value here would load the model at shutdown for an install that never used the feature.
    public void Dispose()
    {
        if (_detector.IsValueCreated && _detector.Value is IDisposable detector) detector.Dispose();
        if (_embedder.IsValueCreated && _embedder.Value is IDisposable embedder) embedder.Dispose();
    }
}
