using System.Runtime.InteropServices;
using OpenCvSharp;

namespace Neuterradise.Profiling.Worker.FaceAnalysis;

public sealed class SFaceEmbeddingExtractor : IFaceEmbeddingAdapter
{
    public const string ModelId = "sface";

    public const string ModelVersion = "2021dec";

    public const string ArtifactFileName = "face_recognition_sface_2021dec.onnx";

    public const string Sha256 = "0ba9fbfa01b5270c96627c4ef784da859931e02f04419c829e83484087c34e79";

    public const int EmbeddingValueCount = 128;

    public const string BaselineEmbeddingSpaceKey = "sface|2021dec|embedding-v1|l2-v1";

    private readonly FaceRecognizerSF _recognizer;

    string IFaceEmbeddingAdapter.ModelId => ModelId;

    string IFaceEmbeddingAdapter.ModelVersion => ModelVersion;

    bool IFaceEmbeddingAdapter.IsAvailable => true;

    public SFaceEmbeddingExtractor(string modelPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(modelPath);
        FaceModelValidator.ValidateOrThrow(modelPath, Sha256, "SFace face recognizer");
        _recognizer = FaceRecognizerSF.Create(Path.GetFullPath(modelPath), string.Empty, OpenCvSharp.Dnn.Backend.OPENCV, OpenCvSharp.Dnn.Target.CPU);
    }

    public IReadOnlyList<float> ExtractEmbedding(IFaceImage image, FaceDetection detection)
    {
        ArgumentNullException.ThrowIfNull(image);
        ArgumentNullException.ThrowIfNull(detection);
        if (image is not OpenCvFaceImage openCvImage)
        {
            throw new ArgumentException("SFaceEmbeddingExtractor requires an OpenCvFaceImage.", nameof(image));
        }

        using var faceBox = BuildFaceRow(detection);
        using var aligned = new Mat();
        _recognizer.AlignCrop(openCvImage.Mat, faceBox, aligned);

        using var feature = new Mat();
        _recognizer.Feature(aligned, feature);

        if (feature.Rows != 1 || feature.Cols != EmbeddingValueCount || feature.Total() != EmbeddingValueCount)
        {
            throw new FaceModelsUnavailableException(
                $"SFace model produced a {feature.Rows}x{feature.Cols} feature instead of exactly 1x{EmbeddingValueCount}.");
        }

        var values = new float[EmbeddingValueCount];
        Marshal.Copy(feature.Data, values, 0, EmbeddingValueCount);

        for (var index = 0; index < values.Length; index++)
        {
            if (float.IsNaN(values[index]) || float.IsInfinity(values[index]))
            {
                throw new FaceModelsUnavailableException($"SFace model produced a non-finite embedding value at index {index}.");
            }
        }

        return values;
    }

    private static Mat BuildFaceRow(FaceDetection detection)
    {
        var row = new float[15];
        row[0] = detection.Bounds.X;
        row[1] = detection.Bounds.Y;
        row[2] = detection.Bounds.Width;
        row[3] = detection.Bounds.Height;

        for (var landmark = 0; landmark < detection.Landmarks.Count && landmark < YuNetFaceDetector.LandmarkCount; landmark++)
        {
            row[4 + (landmark * 2)] = detection.Landmarks[landmark].X;
            row[5 + (landmark * 2)] = detection.Landmarks[landmark].Y;
        }

        row[14] = detection.Confidence;

        var mat = new Mat(1, 15, MatType.CV_32FC1);
        Marshal.Copy(row, 0, mat.Data, row.Length);
        return mat;
    }

    public void Dispose()
    {
        _recognizer.Dispose();
    }
}

public interface IFaceEmbeddingAdapter : IDisposable
{
    string ModelId { get; }

    string ModelVersion { get; }

    bool IsAvailable { get; }

    IReadOnlyList<float> ExtractEmbedding(IFaceImage image, FaceDetection detection);
}
