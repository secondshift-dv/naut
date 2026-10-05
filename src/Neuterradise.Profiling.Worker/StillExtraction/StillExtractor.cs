using Neuterradise.Profiling.Protocol;
using System.Collections.Concurrent;
using System.Security.Cryptography;
using OpenCvSharp;
using Neuterradise.Profiling.Worker.FaceAnalysis;

namespace Neuterradise.Profiling.Worker.StillExtraction;
public sealed class StillExtractor
{

    public const int MaximumEdgePixels = 2048;

    public const int MaximumRequestsPerCall = 24;

    private const int _jpegQuality = 85;

    private readonly IFaceImageSource _imageSource;

    public StillExtractor(IFaceImageSource? imageSource = null)
    {
        _imageSource = imageSource ?? new OpenCvFaceImageSource();
    }

    public ExtractStillsResult Extract(ExtractStillsRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var diagnostics = new List<string>();
        var items = request.Requests
            .Where(item => !string.IsNullOrWhiteSpace(item.RequestKey))
            .Take(MaximumRequestsPerCall)
            .ToArray();
        if (items.Length == 0)
        {
            return new ExtractStillsResult { MediaId = request.MediaId, Diagnostics = ["no still was requested."] };
        }

        var stills = new List<ExtractedStill>(items.Length);
        try
        {
            if (request.IsVideo)
            {

                var timestamps = items
                    .Select(item => item.TimestampMilliseconds ?? 0)
                    .Distinct()
                    .OrderBy(static value => value)
                    .ToArray();
                var plan = new VideoSamplePlan { SampleTimestampMilliseconds = timestamps };
                var samples = _imageSource.LoadVideoSamples(request.InputPath, plan);
                var byTimestamp = samples.ToDictionary(
                    static sample => sample.TimestampMilliseconds,
                    static sample => sample.Frame);

                try
                {
                    foreach (var item in items)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        var wanted = item.TimestampMilliseconds ?? 0;
                        if (!byTimestamp.TryGetValue(wanted, out var frame))
                        {
                            diagnostics.Add($"no frame could be decoded at {wanted} ms.");
                            continue;
                        }

                        if (TryEncode(frame, item, out var still, diagnostics))
                        {
                            stills.Add(still with { TimestampMilliseconds = wanted });
                        }
                    }
                }
                finally
                {
                    foreach (var frame in byTimestamp.Values)
                    {
                        frame.Dispose();
                    }
                }
            }
            else
            {
                using var image = _imageSource.LoadImage(request.InputPath);
                foreach (var item in items)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (TryEncode(image, item, out var still, diagnostics))
                    {
                        stills.Add(still);
                    }
                }
            }
        }
        catch (FaceInputException ex)
        {
            diagnostics.Add(ex.Message);
        }

        return new ExtractStillsResult
        {
            MediaId = request.MediaId,
            Stills = stills,
            Diagnostics = diagnostics,
        };
    }

    private static bool TryEncode(
        IFaceImage image,
        StillExtractionRequestItem item,
        out ExtractedStill still,
        List<string> diagnostics)
    {
        still = new ExtractedStill();
        if (image is not OpenCvFaceImage openCv)
        {
            diagnostics.Add("the decoded frame is not an OpenCV image.");
            return false;
        }

        var source = openCv.Mat;
        Mat? cropped = null;
        Mat? resized = null;
        try
        {
            var working = source;
            if (item.Crop is { } crop)
            {
                var x = Math.Clamp(crop.X, 0, Math.Max(0, source.Width - 1));
                var y = Math.Clamp(crop.Y, 0, Math.Max(0, source.Height - 1));
                var width = Math.Clamp(crop.Width, 1, source.Width - x);
                var height = Math.Clamp(crop.Height, 1, source.Height - y);
                cropped = new Mat(source, new Rect(x, y, width, height));
                working = cropped;
            }

            var maxEdge = Math.Clamp(item.MaxEdgePixels, 16, MaximumEdgePixels);
            var longest = Math.Max(working.Width, working.Height);
            if (longest > maxEdge)
            {
                var scale = maxEdge / (double)longest;
                var targetWidth = Math.Max(1, (int)Math.Round(working.Width * scale));
                var targetHeight = Math.Max(1, (int)Math.Round(working.Height * scale));
                resized = new Mat();
                Cv2.Resize(working, resized, new Size(targetWidth, targetHeight), interpolation: InterpolationFlags.Area);
                working = resized;
            }

            if (!Cv2.ImEncode(
                    ".jpg",
                    working,
                    out var encoded,
                    [new ImageEncodingParam(ImwriteFlags.JpegQuality, _jpegQuality)])
                || encoded.Length == 0)
            {
                diagnostics.Add($"still '{item.RequestKey}' could not be encoded.");
                return false;
            }

            still = new ExtractedStill
            {
                RequestKey = item.RequestKey,
                JpegBase64 = Convert.ToBase64String(encoded),
                Width = working.Width,
                Height = working.Height,
            };
            return true;
        }
        catch (OpenCVException ex)
        {
            diagnostics.Add($"still '{item.RequestKey}' failed: {ex.Message}");
            return false;
        }
        finally
        {
            resized?.Dispose();
            cropped?.Dispose();
        }
    }
}
