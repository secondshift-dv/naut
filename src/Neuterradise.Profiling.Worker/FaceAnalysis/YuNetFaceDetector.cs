using Neuterradise.Profiling.Protocol;
using System.Collections.Concurrent;
using System.Security.Cryptography;
using OpenCvSharp;

namespace Neuterradise.Profiling.Worker.FaceAnalysis;

public sealed class YuNetFaceDetector : IFaceDetectorAdapter
{
    public const string ModelId = "yunet";

    public const string ModelVersion = "2023mar";

    public const string ArtifactFileName = "face_detection_yunet_2023mar.onnx";

    public const string Sha256 = "8f2383e4dd3cfbb4553ea8718107fc0423210dc964f9f4280604804ed2552fa4";

    public const float ScoreThreshold = 0.9f;

    public const float NmsThreshold = 0.3f;

    public const int TopK = 5000;

    public const int LandmarkCount = 5;

    private readonly string _modelPath;
    private readonly ConcurrentDictionary<(int Width, int Height), FaceDetectorYN> _detectors = new();

    string IFaceDetectorAdapter.ModelId => ModelId;

    string IFaceDetectorAdapter.ModelVersion => ModelVersion;

    bool IFaceDetectorAdapter.IsAvailable => true;

    public YuNetFaceDetector(string modelPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(modelPath);
        FaceModelValidator.ValidateOrThrow(modelPath, Sha256, "YuNet face detector");
        _modelPath = Path.GetFullPath(modelPath);
    }

    public IReadOnlyList<FaceDetection> Detect(IFaceImage image)
    {
        ArgumentNullException.ThrowIfNull(image);
        if (image is not OpenCvFaceImage openCvImage)
        {
            throw new ArgumentException("YuNetFaceDetector requires an OpenCvFaceImage.", nameof(image));
        }

        var detector = _detectors.GetOrAdd(
            (openCvImage.Width, openCvImage.Height),
            size => FaceDetectorYN.Create(
                _modelPath,
                string.Empty,
                new Size(size.Width, size.Height),
                ScoreThreshold,
                NmsThreshold,
                TopK,
                OpenCvSharp.Dnn.Backend.OPENCV,
                OpenCvSharp.Dnn.Target.CPU));

        using var faces = new Mat();
        if (detector.Detect(openCvImage.Mat, faces) == 0 || faces.Rows == 0)
        {
            return [];
        }

        var rows = faces.Rows;
        var columns = faces.Cols;
        var detections = new List<FaceDetection>(rows);
        var row = new float[columns];

        for (var faceIndex = 0; faceIndex < rows; faceIndex++)
        {
            for (var column = 0; column < columns; column++)
            {
                row[column] = faces.At<float>(faceIndex, column);
            }

            var bounds = new FaceBounds
            {
                X = (int)Math.Round(row[0]),
                Y = (int)Math.Round(row[1]),
                Width = (int)Math.Round(row[2]),
                Height = (int)Math.Round(row[3])
            };

            var landmarks = new FacePoint[LandmarkCount];
            for (var landmark = 0; landmark < LandmarkCount; landmark++)
            {
                landmarks[landmark] = new FacePoint
                {
                    X = (int)Math.Round(row[4 + (landmark * 2)]),
                    Y = (int)Math.Round(row[5 + (landmark * 2)])
                };
            }

            detections.Add(new FaceDetection
            {
                Bounds = bounds,
                Landmarks = landmarks,
                Confidence = row[14]
            });
        }

        return detections;
    }

    public void Dispose()
    {
        foreach (var detector in _detectors.Values)
        {
            detector.Dispose();
        }

        _detectors.Clear();
    }
}

public sealed record FaceDetection
{
    public FaceBounds Bounds { get; init; } = new();

    public IReadOnlyList<FacePoint> Landmarks { get; init; } = [];

    public float Confidence { get; init; }
}

public interface IFaceDetectorAdapter : IDisposable
{
    string ModelId { get; }

    string ModelVersion { get; }

    bool IsAvailable { get; }

    IReadOnlyList<FaceDetection> Detect(IFaceImage image);
}

public static class FaceModelValidator
{
    public static void ValidateOrThrow(string modelPath, string expectedSha256, string logicalName)
    {
        if (!File.Exists(modelPath))
        {
            throw new FaceModelsUnavailableException($"{logicalName} model is missing: {modelPath}.");
        }

        var actual = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(modelPath))).ToLowerInvariant();
        if (!string.Equals(actual, expectedSha256, StringComparison.OrdinalIgnoreCase))
        {
            throw new FaceModelsUnavailableException(
                $"{logicalName} model at {modelPath} is checksum-invalid; expected {expectedSha256}, found {actual}.");
        }
    }
}

public sealed class OpenCvFaceImage : IFaceImage
{
    public Mat Mat { get; }

    public int Width => Mat.Width;

    public int Height => Mat.Height;

    public OpenCvFaceImage(Mat mat)
    {
        Mat = mat ?? throw new ArgumentNullException(nameof(mat));
    }

    public void Dispose()
    {
        Mat.Dispose();
    }
}
