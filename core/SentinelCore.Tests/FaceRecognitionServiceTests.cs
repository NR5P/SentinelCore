using Microsoft.Extensions.Logging;
using Microsoft.ML.OnnxRuntime;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats.Jpeg;
using SixLabors.ImageSharp.PixelFormats;
using Xunit;

namespace SentinelCore.Tests;

// These exist because of a bug that no other test could have caught: FaceAiSharp's bundle factory
// locates its ONNX models relative to Assembly.Location, which is empty in a single-file publish,
// so every release .zip died on startup with an ArgumentNullException while the git-checkout build
// - the only one anything ever ran - was perfectly fine.
//
// The models are now resolved from AppContext.BaseDirectory, and loaded lazily so that a model
// problem costs this one opt-in feature rather than the whole process. That laziness is exactly
// why the second test matters: nothing at startup would notice a wrong filename any more.
public sealed class FaceRecognitionServiceTests
{
    // Records whether anything was logged at Warning or above. DetectFaceEmbeddings deliberately
    // swallows every failure and returns no faces, so "returned an empty list" cannot on its own
    // distinguish a working model from one that never loaded - the log is the only signal.
    private sealed class WarningRecorder : ILogger<FaceRecognitionService>
    {
        public List<Exception?> Warnings { get; } = [];
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(
            LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (logLevel >= LogLevel.Warning) Warnings.Add(exception);
        }
    }

    private static byte[] BlankImage(int width = 128, int height = 128)
    {
        using var image = new Image<Rgb24>(width, height, new Rgb24(32, 32, 32));
        using var buffer = new MemoryStream();
        image.Save(buffer, new JpegEncoder());
        return buffer.ToArray();
    }

    // An image with no face in it should come back with no embeddings - and, critically, without
    // having logged anything, which is what tells us the detector really ran rather than failing
    // to load and being swallowed.
    [Fact]
    public void LoadsItsModelsAndRunsOnAnImageWithNoFaces()
    {
        var logger = new WarningRecorder();
        var service = new FaceRecognitionService(logger);

        var embeddings = service.DetectFaceEmbeddings(BlankImage());

        Assert.Empty(embeddings);
        Assert.Empty(logger.Warnings);
    }

    // Constructing the service must not touch the models: it is resolved as an eagerly-created
    // singleton in both hosts, so anything the constructor can throw is fatal to a process whose
    // job is watching cameras. This is the guard on that arrangement, not on the models.
    [Fact]
    public void ConstructionDoesNotLoadAnything()
    {
        var logger = new WarningRecorder();

        var exception = Record.Exception(() => new FaceRecognitionService(logger));

        Assert.Null(exception);
        Assert.Empty(logger.Warnings);
    }

    // Malformed bytes are the ordinary case this has always handled - kept alongside the above so
    // the "degrade, don't throw" contract is pinned rather than assumed.
    [Fact]
    public void DegradesToNoFacesOnUndecodableBytes()
    {
        var logger = new WarningRecorder();
        var service = new FaceRecognitionService(logger);

        var embeddings = service.DetectFaceEmbeddings([1, 2, 3, 4]);

        Assert.Empty(embeddings);
        Assert.Single(logger.Warnings);
    }

    // YuNet's decode indexes twelve outputs by name (cls_/obj_/bbox_/kps_ per stride). Get one
    // wrong - or swap in a differently-exported YuNet - and DetectFaces throws KeyNotFoundException
    // deep inside a per-alert path where DetectFaceEmbeddings catches everything and logs a
    // warning, so face recognition would silently find nobody while looking healthy. This asserts
    // the contract against the model file actually shipping in the build output.
    [Fact]
    public void ShippedDetectorModelExposesEveryOutputTheDecodeIndexes()
    {
        var modelPath = Path.Combine(AppContext.BaseDirectory, "onnx", "face_detection_yunet_2023mar.onnx");
        Assert.True(File.Exists(modelPath), $"Detector model missing from the build output at {modelPath}");

        using var session = new InferenceSession(modelPath);
        var outputs = session.OutputMetadata.Keys.ToHashSet(StringComparer.Ordinal);

        foreach (var stride in new[] { 8, 16, 32 })
        {
            Assert.Contains($"cls_{stride}", outputs);
            Assert.Contains($"obj_{stride}", outputs);
            Assert.Contains($"bbox_{stride}", outputs);
            Assert.Contains($"kps_{stride}", outputs);
        }

        // The decode letterboxes to a fixed 640x640 because this export's input is static. If a
        // future model file is dynamic (or a different size), YuNetFaceDetector.InputSize and the
        // stride grids have to move with it rather than silently mis-scaling every coordinate.
        Assert.Equal([1, 3, 640, 640], session.InputMetadata.Values.Single().Dimensions);
    }

    // The release bundle ships the models only in SentinelCore/onnx; the dashboard beside it must
    // still find them there, or enrolling a known face breaks in every release install.
    [Fact]
    public void FindModel_FallsBackToTheMainAppsModels_InAReleaseBundle()
    {
        var bundle = Directory.CreateTempSubdirectory("bundle-").FullName;
        try
        {
            var web = Directory.CreateDirectory(Path.Combine(bundle, "SentinelCore.Web")).FullName;
            var models = Directory.CreateDirectory(Path.Combine(bundle, "SentinelCore", "onnx")).FullName;
            File.WriteAllText(Path.Combine(models, "model.onnx"), "");

            Assert.Equal(Path.Combine(models, "model.onnx"), FaceRecognitionService.FindModel(web, "model.onnx"));
            Assert.Null(FaceRecognitionService.FindModel(web, "missing.onnx"));

            // A build output's own copy wins.
            var own = Directory.CreateDirectory(Path.Combine(web, "onnx")).FullName;
            File.WriteAllText(Path.Combine(own, "model.onnx"), "");
            Assert.Equal(Path.Combine(own, "model.onnx"), FaceRecognitionService.FindModel(web, "model.onnx"));
        }
        finally
        {
            Directory.Delete(bundle, recursive: true);
        }
    }
}
