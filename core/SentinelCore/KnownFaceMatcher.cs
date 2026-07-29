namespace SentinelCore;

// Matches a face embedding detected in an alert image against the configured KnownFaces list.
// Unlike KnownVehicleMatcher's exact string comparison, this is a nearest-neighbor search over
// cosine similarity, since two photos of the same person never produce identical embeddings.
// The threshold (FaceAiSharp's own published guidance: >=0.42 same person, 0.28-0.42 uncertain,
// <=0.28 different people, for its ArcFace embeddings) is deliberately conservative - a missed
// match just falls back to "unrecognized", while a wrong match would mislabel a stranger as a
// known person, which is worse.
public static class KnownFaceMatcher
{
    public const float SimilarityThreshold = 0.42f;

    public static string? Match(IReadOnlyList<KnownFace> knownFaces, float[] embedding)
    {
        string? bestLabel = null;
        var bestSimilarity = SimilarityThreshold;

        foreach (var face in knownFaces)
        {
            foreach (var enrollment in face.Enrollments)
            {
                var similarity = CosineSimilarity(enrollment.Embedding, embedding);
                if (similarity >= bestSimilarity)
                {
                    bestSimilarity = similarity;
                    bestLabel = face.Label;
                }
            }
        }

        return bestLabel;
    }

    private static float CosineSimilarity(float[] a, float[] b)
    {
        if (a.Length == 0 || a.Length != b.Length) return 0f;

        float dot = 0f, normA = 0f, normB = 0f;
        for (var i = 0; i < a.Length; i++)
        {
            dot += a[i] * b[i];
            normA += a[i] * a[i];
            normB += b[i] * b[i];
        }

        if (normA == 0f || normB == 0f) return 0f;
        return dot / (MathF.Sqrt(normA) * MathF.Sqrt(normB));
    }
}
