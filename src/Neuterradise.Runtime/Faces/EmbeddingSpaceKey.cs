using System.Buffers.Binary;
using System.Globalization;

namespace Neuterradise.App.Faces;

public readonly record struct EmbeddingSpaceKey(
    string ModelId,
    string ModelVersion,
    string EmbeddingSchemaVersion,
    string NormalizationVersion)
{

    public const char ComponentSeparator = '|';

    public const int ComponentCount = 4;

    public static EmbeddingSpaceKey Baseline { get; } = new(
        "sface",
        "2021dec",
        FaceEmbedding.SchemaVersionV1,
        FaceEmbedding.NormalizationVersionL2);

    public bool IsValid =>
        IsCanonicalComponent(ModelId)
        && IsCanonicalComponent(ModelVersion)
        && IsCanonicalComponent(EmbeddingSchemaVersion)
        && IsCanonicalComponent(NormalizationVersion);

    public string Canonical
    {
        get
        {
            EnsureValid(this, nameof(Canonical));
            return string.Join(
                ComponentSeparator,
                ModelId,
                ModelVersion,
                EmbeddingSchemaVersion,
                NormalizationVersion);
        }
    }

    public static EmbeddingSpaceKey Create(
        string modelId,
        string modelVersion,
        string embeddingSchemaVersion,
        string normalizationVersion)
    {
        var key = new EmbeddingSpaceKey(
            modelId,
            modelVersion,
            embeddingSchemaVersion,
            normalizationVersion);
        EnsureValid(key, nameof(key));
        return key;
    }

    public static EmbeddingSpaceKey Parse(string value)
    {
        if (!TryParse(value, out var key))
        {
            throw new FormatException(
                $"'{value}' is not a canonical EmbeddingSpaceKey serialization.");
        }

        return key;
    }

    public static bool TryParse(string? value, out EmbeddingSpaceKey key)
    {
        key = default;

        if (string.IsNullOrEmpty(value))
        {
            return false;
        }

        var parts = value.Split(ComponentSeparator);
        if (parts.Length != ComponentCount)
        {
            return false;
        }

        var candidate = new EmbeddingSpaceKey(parts[0], parts[1], parts[2], parts[3]);
        if (!candidate.IsValid)
        {
            return false;
        }

        key = candidate;
        return true;
    }

    public bool IsCompatibleWith(EmbeddingSpaceKey other) =>
        IsValid && other.IsValid && Equals(other);

    public static void EnsureCompatible(EmbeddingSpaceKey left, EmbeddingSpaceKey right)
    {
        EnsureValid(left, nameof(left));
        EnsureValid(right, nameof(right));

        if (!left.Equals(right))
        {
            throw new EmbeddingSpaceMismatchException(left, right);
        }
    }

    public static void EnsureValid(EmbeddingSpaceKey key, string parameterName)
    {
        if (!key.IsValid)
        {
            throw new ArgumentException(
                "An EmbeddingSpaceKey requires a canonical ModelId, ModelVersion, "
                + "EmbeddingSchemaVersion, and NormalizationVersion.",
                parameterName);
        }
    }

    public override string ToString() => IsValid ? Canonical : "<invalid EmbeddingSpaceKey>";

    private static bool IsCanonicalComponent(string? component)
    {
        if (string.IsNullOrWhiteSpace(component))
        {
            return false;
        }

        if (component.Length != component.Trim().Length)
        {
            return false;
        }

        foreach (var character in component)
        {
            if (character == ComponentSeparator || char.IsControl(character))
            {
                return false;
            }
        }

        return true;
    }
}

public sealed class EmbeddingSpaceMismatchException : InvalidOperationException
{
    public EmbeddingSpaceMismatchException(EmbeddingSpaceKey left, EmbeddingSpaceKey right)
        : base(
            $"Embeddings in '{Describe(left)}' cannot be compared with embeddings in "
            + $"'{Describe(right)}'. Embedding space compatibility is exact.")
    {
        Left = left;
        Right = right;
    }

    public EmbeddingSpaceKey Left { get; }

    public EmbeddingSpaceKey Right { get; }

    private static string Describe(EmbeddingSpaceKey key) => key.ToString();
}

public static class FaceEmbedding
{

    public const string SchemaVersionV1 = "embedding-v1";

    public const int DimensionV1 = 128;

    public const string NormalizationVersionL2 = "l2-v1";

    private const int BytesPerValue = sizeof(float);

    public static int? RequiredDimension(EmbeddingSpaceKey space)
    {
        EmbeddingSpaceKey.EnsureValid(space, nameof(space));
        return string.Equals(space.EmbeddingSchemaVersion, SchemaVersionV1, StringComparison.Ordinal)
            ? DimensionV1
            : null;
    }

    public static byte[] ToBlob(ReadOnlySpan<float> values)
    {
        if (values.Length == 0)
        {
            throw new ArgumentException("An embedding cannot be empty.", nameof(values));
        }

        var blob = new byte[values.Length * BytesPerValue];
        for (var index = 0; index < values.Length; index++)
        {
            if (!float.IsFinite(values[index]))
            {
                throw new ArgumentException(
                    $"Embedding value at index {index} is not finite.",
                    nameof(values));
            }

            BinaryPrimitives.WriteSingleLittleEndian(
                blob.AsSpan(index * BytesPerValue, BytesPerValue),
                values[index]);
        }

        return blob;
    }

    public static float[] FromBlob(ReadOnlySpan<byte> blob, EmbeddingSpaceKey space)
    {
        if (!TryFromBlob(blob, space, out var values, out var failure))
        {
            throw new FormatException(failure);
        }

        return values;
    }

    public static bool TryFromBlob(
        ReadOnlySpan<byte> blob,
        EmbeddingSpaceKey space,
        out float[] values,
        out string? failure)
    {
        values = [];
        failure = null;

        if (!space.IsValid)
        {
            failure = "The embedding space is not canonical.";
            return false;
        }

        if (blob.Length == 0 || blob.Length % BytesPerValue != 0)
        {
            failure = string.Create(
                CultureInfo.InvariantCulture,
                $"An embedding blob of {blob.Length} bytes is not a whole float32 vector.");
            return false;
        }

        var count = blob.Length / BytesPerValue;
        if (RequiredDimension(space) is int required && count != required)
        {
            failure = string.Create(
                CultureInfo.InvariantCulture,
                $"Embedding schema '{space.EmbeddingSchemaVersion}' requires {required} values but the blob carries {count}.");
            return false;
        }

        var parsed = new float[count];
        for (var index = 0; index < count; index++)
        {
            var value = BinaryPrimitives.ReadSingleLittleEndian(
                blob.Slice(index * BytesPerValue, BytesPerValue));
            if (!float.IsFinite(value))
            {
                failure = string.Create(
                    CultureInfo.InvariantCulture,
                    $"Embedding value at index {index} is not finite.");
                return false;
            }

            parsed[index] = value;
        }

        values = parsed;
        return true;
    }

    public static double CosineSimilarity(
        EmbeddingSpaceKey leftSpace,
        ReadOnlySpan<float> left,
        EmbeddingSpaceKey rightSpace,
        ReadOnlySpan<float> right)
    {
        EmbeddingSpaceKey.EnsureCompatible(leftSpace, rightSpace);

        if (left.Length != right.Length)
        {
            throw new ArgumentException(
                $"Compatible embeddings must have equal length, but {left.Length} and {right.Length} were given.",
                nameof(right));
        }

        if (left.Length == 0)
        {
            throw new ArgumentException("An embedding cannot be empty.", nameof(left));
        }

        double dot = 0;
        double leftNorm = 0;
        double rightNorm = 0;

        for (var index = 0; index < left.Length; index++)
        {
            double leftValue = left[index];
            double rightValue = right[index];

            if (!double.IsFinite(leftValue) || !double.IsFinite(rightValue))
            {
                throw new ArgumentException(
                    $"Embedding value at index {index} is not finite.",
                    nameof(left));
            }

            dot += leftValue * rightValue;
            leftNorm += leftValue * leftValue;
            rightNorm += rightValue * rightValue;
        }

        if (leftNorm <= 0 || rightNorm <= 0)
        {
            return 0;
        }

        return dot / (Math.Sqrt(leftNorm) * Math.Sqrt(rightNorm));
    }
}
