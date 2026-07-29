using Xunit;

namespace SentinelCore.Tests;

public sealed class KnownFaceMatcherTests
{
    [Fact]
    public void Match_ReturnsLabel_WhenEmbeddingIsIdentical()
    {
        var embedding = NormalizedEmbedding([1, 0, 0]);
        var knownFaces = new List<KnownFace>
        {
            new() { Label = "Nathan", Enrollments = [new KnownFaceEnrollment { Embedding = embedding }] }
        };

        var match = KnownFaceMatcher.Match(knownFaces, embedding);

        Assert.Equal("Nathan", match);
    }

    [Fact]
    public void Match_ReturnsNull_WhenNoEnrollmentIsCloseEnough()
    {
        var knownFaces = new List<KnownFace>
        {
            new() { Label = "Nathan", Enrollments = [new KnownFaceEnrollment { Embedding = NormalizedEmbedding([1, 0, 0]) }] }
        };

        // Orthogonal to the enrolled embedding - cosine similarity 0, well below the threshold.
        var match = KnownFaceMatcher.Match(knownFaces, NormalizedEmbedding([0, 1, 0]));

        Assert.Null(match);
    }

    [Fact]
    public void Match_ReturnsNull_WhenNoKnownFacesAreConfigured()
    {
        var match = KnownFaceMatcher.Match([], NormalizedEmbedding([1, 0, 0]));

        Assert.Null(match);
    }

    [Fact]
    public void Match_PicksTheClosestEnrollment_AcrossMultiplePeopleAndPhotos()
    {
        var target = NormalizedEmbedding([1, 0, 0]);
        var knownFaces = new List<KnownFace>
        {
            new()
            {
                Label = "Terry",
                Enrollments = [new KnownFaceEnrollment { Embedding = NormalizedEmbedding([0.5f, 0.5f, 0]) }]
            },
            new()
            {
                Label = "Nathan",
                Enrollments =
                [
                    new KnownFaceEnrollment { Embedding = NormalizedEmbedding([0, 1, 0]) },
                    new KnownFaceEnrollment { Embedding = NormalizedEmbedding([0.95f, 0.05f, 0]) }
                ]
            }
        };

        var match = KnownFaceMatcher.Match(knownFaces, target);

        Assert.Equal("Nathan", match);
    }

    private static float[] NormalizedEmbedding(float[] vector)
    {
        var length = MathF.Sqrt(vector.Sum(v => v * v));
        return vector.Select(v => v / length).ToArray();
    }
}
