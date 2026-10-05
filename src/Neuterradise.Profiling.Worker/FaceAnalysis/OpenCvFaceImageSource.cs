using Neuterradise.Profiling.Protocol;
using OpenCvSharp;

namespace Neuterradise.Profiling.Worker.FaceAnalysis;
public sealed class OpenCvFaceImageSource : IFaceImageSource
{
    private const int MaximumDiscoverySamples = 12;
    private const int MaximumShortlistSamples = 4;

    public IFaceImage LoadImage(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        if (!File.Exists(path))
        {
            throw new FaceInputException($"Analyzed image does not exist: {path}.");
        }

        var mat = Cv2.ImRead(path, ImreadModes.Color);
        if (mat is null || mat.Empty())
        {
            mat?.Dispose();
            throw new FaceInputException($"Analyzed image could not be decoded: {path}.");
        }

        return new OpenCvFaceImage(mat);
    }

    public IReadOnlyList<FaceFrameSample> LoadVideoSamples(string path, VideoSamplePlan plan)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(plan);
        if (plan.IsEmpty)
        {
            throw new FaceInputException("A VIDEO sample plan with no timestamps cannot be decoded.");
        }

        if (!File.Exists(path))
        {
            throw new FaceInputException($"Analyzed video does not exist: {path}.");
        }

        using var capture = new VideoCapture(path);
        if (!capture.IsOpened())
        {
            throw new FaceInputException($"Analyzed video could not be opened: {path}.");
        }

        var discovered = new List<(long Timestamp, OpenCvFaceImage Frame, Mat Signature, double Contrast)>();
        try
        {
            foreach (var timestamp in plan.SampleTimestampMilliseconds.Distinct().Order().Take(MaximumDiscoverySamples))
            {
                capture.Set(VideoCaptureProperties.PosMsec, timestamp);
                using var frame = new Mat();
                if (!capture.Read(frame) || frame.Empty())
                {
                    continue;
                }

                using var gray = new Mat();
                Cv2.CvtColor(frame, gray, ColorConversionCodes.BGR2GRAY);
                var signature = new Mat();
                Cv2.Resize(gray, signature, new Size(32, 32), interpolation: InterpolationFlags.Area);
                Cv2.MeanStdDev(signature, out _, out var deviation);
                discovered.Add((timestamp, new OpenCvFaceImage(frame.Clone()), signature, deviation.Val0));
            }

            if (discovered.Count == 0)
            {
                throw new FaceInputException(
                    $"Analyzed video opened, but none of the requested sample frames could be decoded: {path}.");
            }

            var shortlist = new List<(long Timestamp, OpenCvFaceImage Frame, Mat Signature, double Contrast)>();
            foreach (var candidate in discovered
                .OrderByDescending(static sample => sample.Contrast)
                .ThenBy(static sample => sample.Timestamp))
            {
                if (shortlist.Count == MaximumShortlistSamples)
                    break;
                if (candidate.Contrast < 8 && shortlist.Count > 0)
                    continue;
                if (shortlist.Any(selected => MeanDifference(candidate.Signature, selected.Signature) < 8))
                    continue;
                shortlist.Add(candidate);
            }

            if (shortlist.Count == 0)
                shortlist.Add(discovered[0]);

            foreach (var candidate in discovered)
            {
                if (!shortlist.Contains(candidate))
                    candidate.Frame.Dispose();
            }

            return shortlist.OrderBy(static sample => sample.Timestamp)
                .Select(static sample => new FaceFrameSample(sample.Timestamp, sample.Frame))
                .ToArray();
        }
        catch
        {
            foreach (var candidate in discovered)
                candidate.Frame.Dispose();
            throw;
        }
        finally
        {
            foreach (var candidate in discovered)
                candidate.Signature.Dispose();
        }
    }

    private static double MeanDifference(Mat left, Mat right)
    {
        using var difference = new Mat();
        Cv2.Absdiff(left, right, difference);
        return Cv2.Mean(difference).Val0;
    }

    public void Dispose()
    {
    }
}
