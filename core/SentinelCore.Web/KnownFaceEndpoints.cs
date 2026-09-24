using SentinelCore;
using Microsoft.Extensions.Options;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats.Jpeg;

namespace SentinelCore.Web;

public sealed record KnownFaceEnrollmentDto(string Id, string PhotoUrl);
public sealed record KnownFaceDto(string Id, string Label, List<KnownFaceEnrollmentDto> Enrollments);

// Enrollment (label + one or more reference photos per person) is richer than Known Vehicles'
// flat plate/label list - it involves photo uploads and per-photo delete, so it gets its own
// CRUD group here instead of a single PUT-the-whole-list endpoint under /api/settings. The
// underlying storage is still the main app's appsettings.json "KnownFaces" section (same as
// KnownVehicles), so both AlertProcessor's matching and Settings -> Backup pick it up for free -
// see SettingsEndpoints' KnownFaces snapshot field.
public static class KnownFaceEndpoints
{
    public static void MapKnownFaceEndpoints(this WebApplication app)
    {
        var group = app.MapGroup("/api/known-faces").RequireAuthorization("AdminOnly");

        group.MapGet("", async (AppSettingsFile mainAppSettings, CancellationToken ct) =>
        {
            var faces = await mainAppSettings.GetSectionAsync<List<KnownFace>>("KnownFaces", ct) ?? [];
            return Results.Ok(faces.Select(ToDto));
        });

        group.MapPost("", async (
            HttpRequest request,
            AppSettingsFile mainAppSettings,
            IFaceRecognitionService faceRecognition,
            IOptions<BridgeOptions> bridgeOptions,
            IHostEnvironment environment,
            CancellationToken ct) =>
        {
            if (!request.HasFormContentType)
                return Results.BadRequest(new { error = "Use multipart/form-data with a label field and an image file." });

            var form = await request.ReadFormAsync(ct);
            var label = form["label"].ToString().Trim();
            if (string.IsNullOrWhiteSpace(label))
                return Results.BadRequest(new { error = "A label is required, e.g. a person's name." });

            var image = form.Files.GetFile("image") ?? form.Files.FirstOrDefault();
            if (image is null || image.Length == 0)
                return Results.BadRequest(new { error = "No image was supplied." });

            var (embedding, error) = await ExtractSingleFaceEmbeddingAsync(image, faceRecognition, ct);
            if (error is not null) return Results.BadRequest(new { error });

            var faceId = Guid.NewGuid().ToString("N");
            var enrollmentId = Guid.NewGuid().ToString("N");
            var relativePhotoPath = await SavePhotoAsync(image, faceId, enrollmentId, bridgeOptions.Value, environment, ct);

            var faces = await mainAppSettings.GetSectionAsync<List<KnownFace>>("KnownFaces", ct) ?? [];
            var face = new KnownFace
            {
                Id = faceId,
                Label = label,
                Enrollments = [new KnownFaceEnrollment { Id = enrollmentId, ImagePath = relativePhotoPath, Embedding = embedding! }]
            };
            faces.Add(face);
            await mainAppSettings.ReplaceSectionAsync("KnownFaces", faces, ct);

            return Results.Ok(ToDto(face));
        }).DisableAntiforgery();

        group.MapPost("/{id}/enrollments", async (
            string id,
            HttpRequest request,
            AppSettingsFile mainAppSettings,
            IFaceRecognitionService faceRecognition,
            IOptions<BridgeOptions> bridgeOptions,
            IHostEnvironment environment,
            CancellationToken ct) =>
        {
            if (!request.HasFormContentType)
                return Results.BadRequest(new { error = "Use multipart/form-data with an image file." });

            var faces = await mainAppSettings.GetSectionAsync<List<KnownFace>>("KnownFaces", ct) ?? [];
            var face = faces.FirstOrDefault(f => f.Id == id);
            if (face is null) return Results.NotFound();

            var form = await request.ReadFormAsync(ct);
            var image = form.Files.GetFile("image") ?? form.Files.FirstOrDefault();
            if (image is null || image.Length == 0)
                return Results.BadRequest(new { error = "No image was supplied." });

            var (embedding, error) = await ExtractSingleFaceEmbeddingAsync(image, faceRecognition, ct);
            if (error is not null) return Results.BadRequest(new { error });

            var enrollmentId = Guid.NewGuid().ToString("N");
            var relativePhotoPath = await SavePhotoAsync(image, face.Id, enrollmentId, bridgeOptions.Value, environment, ct);
            face.Enrollments.Add(new KnownFaceEnrollment { Id = enrollmentId, ImagePath = relativePhotoPath, Embedding = embedding! });
            await mainAppSettings.ReplaceSectionAsync("KnownFaces", faces, ct);

            return Results.Ok(ToDto(face));
        }).DisableAntiforgery();

        group.MapDelete("/{id}/enrollments/{enrollmentId}", async (
            string id,
            string enrollmentId,
            AppSettingsFile mainAppSettings,
            IOptions<BridgeOptions> bridgeOptions,
            IHostEnvironment environment,
            CancellationToken ct) =>
        {
            var faces = await mainAppSettings.GetSectionAsync<List<KnownFace>>("KnownFaces", ct) ?? [];
            var face = faces.FirstOrDefault(f => f.Id == id);
            var enrollment = face?.Enrollments.FirstOrDefault(e => e.Id == enrollmentId);
            if (face is null || enrollment is null) return Results.NotFound();

            face.Enrollments.Remove(enrollment);
            DeletePhotoIfExists(enrollment.ImagePath, bridgeOptions.Value, environment);
            await mainAppSettings.ReplaceSectionAsync("KnownFaces", faces, ct);

            return Results.Ok(ToDto(face));
        });

        group.MapDelete("/{id}", async (
            string id,
            AppSettingsFile mainAppSettings,
            IOptions<BridgeOptions> bridgeOptions,
            IHostEnvironment environment,
            CancellationToken ct) =>
        {
            var faces = await mainAppSettings.GetSectionAsync<List<KnownFace>>("KnownFaces", ct) ?? [];
            var face = faces.FirstOrDefault(f => f.Id == id);
            if (face is null) return Results.NotFound();

            faces.Remove(face);
            await mainAppSettings.ReplaceSectionAsync("KnownFaces", faces, ct);

            var faceDirectory = Path.Combine(ResolveDataRoot(bridgeOptions.Value, environment), "known-faces", face.Id);
            if (Directory.Exists(faceDirectory)) Directory.Delete(faceDirectory, recursive: true);

            return Results.Ok(new { deleted = true });
        });

        group.MapGet("/{id}/enrollments/{enrollmentId}/photo", async (
            string id,
            string enrollmentId,
            AppSettingsFile mainAppSettings,
            IOptions<BridgeOptions> bridgeOptions,
            IHostEnvironment environment,
            CancellationToken ct) =>
        {
            var faces = await mainAppSettings.GetSectionAsync<List<KnownFace>>("KnownFaces", ct) ?? [];
            var enrollment = faces.FirstOrDefault(f => f.Id == id)?.Enrollments.FirstOrDefault(e => e.Id == enrollmentId);
            if (enrollment is null) return Results.NotFound();

            var absolutePath = Path.Combine(ResolveDataRoot(bridgeOptions.Value, environment), enrollment.ImagePath);
            if (!File.Exists(absolutePath)) return Results.NotFound();

            var bytes = await File.ReadAllBytesAsync(absolutePath, ct);
            return Results.File(bytes, "image/jpeg");
        });
    }

    // Enrollment photos must show exactly one clear face - zero means nothing to match against,
    // and more than one means it's ambiguous which face the label is actually for.
    private static async Task<(float[]? Embedding, string? Error)> ExtractSingleFaceEmbeddingAsync(
        IFormFile image, IFaceRecognitionService faceRecognition, CancellationToken ct)
    {
        await using var stream = new MemoryStream();
        await image.CopyToAsync(stream, ct);
        var embeddings = faceRecognition.DetectFaceEmbeddings(stream.ToArray());

        return embeddings.Count switch
        {
            0 => (null, "No clear face was found in that photo. Try a well-lit, front-facing photo of just this person."),
            > 1 => (null, "More than one face was found in that photo. Use a photo with just this person in it."),
            _ => (embeddings[0], null)
        };
    }

    // Normalizes to JPEG regardless of the uploaded format (phones commonly send HEIC/PNG), the
    // same way ImageOptimizer normalizes alert images before they're stored.
    private static async Task<string> SavePhotoAsync(
        IFormFile image, string faceId, string enrollmentId, BridgeOptions bridgeOptions, IHostEnvironment environment, CancellationToken ct)
    {
        var relativePath = Path.Combine("known-faces", faceId, $"{enrollmentId}.jpg");
        var absolutePath = Path.Combine(ResolveDataRoot(bridgeOptions, environment), relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(absolutePath)!);

        await using var source = image.OpenReadStream();
        using var decoded = await Image.LoadAsync(source, ct);
        await decoded.SaveAsync(absolutePath, new JpegEncoder(), ct);

        return relativePath;
    }

    private static void DeletePhotoIfExists(string relativePath, BridgeOptions bridgeOptions, IHostEnvironment environment)
    {
        var absolutePath = Path.Combine(ResolveDataRoot(bridgeOptions, environment), relativePath);
        if (File.Exists(absolutePath)) File.Delete(absolutePath);
    }

    internal static string ResolveDataRoot(BridgeOptions bridgeOptions, IHostEnvironment environment) =>
        Path.IsPathRooted(bridgeOptions.DataDirectory)
            ? bridgeOptions.DataDirectory
            : Path.Combine(environment.ContentRootPath, bridgeOptions.DataDirectory);

    private static KnownFaceDto ToDto(KnownFace face) => new(
        face.Id,
        face.Label,
        face.Enrollments.Select(e => new KnownFaceEnrollmentDto(e.Id, $"/api/known-faces/{face.Id}/enrollments/{e.Id}/photo")).ToList());
}
