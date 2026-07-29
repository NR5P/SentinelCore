using FaceAiSharp;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;

namespace SentinelCore;

public interface IFaceRecognitionService
{
    IReadOnlyList<float[]> DetectFaceEmbeddings(byte[] imageBytes);
}

// Detects faces in an alert image and turns each one into an embedding vector, entirely locally
// via ONNX Runtime (FaceAiSharp's bundled SCRFD detector + ArcFace embedding model) - no network
// call, independent of whichever vision backend (Ollama or cloud) is analyzing the same alert
// for its descriptive summary. Registered as a singleton: both models hold an ONNX Runtime
// session that's expensive to create and safe to reuse, so they're loaded once for the process
// lifetime, the same way Ollama's model is kept loaded between alerts.
public sealed class FaceRecognitionService : IFaceRecognitionService
{
    private readonly IFaceDetectorWithLandmarks _detector;
    private readonly IFaceEmbeddingsGenerator _embedder;
    private readonly ILogger<FaceRecognitionService> _logger;

    public FaceRecognitionService(ILogger<FaceRecognitionService> logger)
    {
        _logger = logger;
        _detector = FaceAiSharpBundleFactory.CreateFaceDetectorWithLandmarks();
        _embedder = FaceAiSharpBundleFactory.CreateFaceEmbeddingsGenerator();
    }

    public IReadOnlyList<float[]> DetectFaceEmbeddings(byte[] imageBytes)
    {
        try
        {
            using var image = Image.Load<Rgb24>(imageBytes);
            var faces = _detector.DetectFaces(image);
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
                _embedder.AlignFaceUsingLandmarks(faceImage, landmarks);
                embeddings.Add(_embedder.GenerateEmbedding(faceImage));
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
}
