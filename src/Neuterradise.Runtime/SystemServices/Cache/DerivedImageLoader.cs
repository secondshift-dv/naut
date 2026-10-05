using System.Diagnostics;
using System.IO;
using Neuterradise.App.SystemServices.Diagnostics;
using SkiaSharp;

namespace Neuterradise.App.SystemServices.Cache;

/// <summary>A decoded image as the renderer wants it, plus its honest memory cost.</summary>
public sealed record DecodedImage(object Image, int PixelWidth, int PixelHeight, long Bytes);

/// <summary>
/// Decodes one derived image at (or near) a target width. The renderer supplies its own decoder so the
/// cache holds renderer-owned image resources; the runtime itself never references a UI framework.
/// </summary>
public interface IDerivedImageDecoder
{
    DecodedImage? Decode(string path, int decodeWidth);
}

/// <summary>
/// Framework-neutral fallback decoder (SkiaSharp). Used headless and by background work; the Uno
/// presentation layer installs its own decoder that produces renderer image sources.
/// </summary>
public sealed class SkiaDerivedImageDecoder : IDerivedImageDecoder
{
    public static SkiaDerivedImageDecoder Instance { get; } = new();

    public DecodedImage? Decode(string path, int decodeWidth)
    {
        using var bitmap = TargetSizeDecoder.Decode(path, decodeWidth);
        if (bitmap is null)
        {
            return null;
        }

        var image = SKImage.FromBitmap(bitmap);
        return new DecodedImage(image, bitmap.Width, bitmap.Height, (long)bitmap.Width * bitmap.Height * 4);
    }
}

/// <summary>
/// Target-size decoding shared by the thumbnail job, the fallback decoder and the renderer adapter:
/// it asks the codec for its cheapest scaled decode at or above the target width, then resizes once.
/// </summary>
public static class TargetSizeDecoder
{
    public static SKBitmap? Decode(string path, int decodeWidth, int? decodeHeight = null)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 64 * 1024);
        return Decode(stream, decodeWidth, decodeHeight);
    }

    public static SKBitmap? Decode(Stream stream, int decodeWidth, int? decodeHeight = null)
    {
        using var managed = new SKManagedStream(stream, disposeManagedStream: false);
        using var codec = SKCodec.Create(managed);
        if (codec is null)
        {
            return null;
        }

        var full = codec.Info;
        if (full.Width <= 0 || full.Height <= 0)
        {
            return null;
        }

        var (targetWidth, targetHeight) = TargetDimensions(full.Width, full.Height, decodeWidth, decodeHeight);
        var scale = (float)targetWidth / full.Width;
        var scaled = codec.GetScaledDimensions(scale);
        if (scaled.Width < targetWidth || scaled.Height < targetHeight)
        {
            scaled = new SKSizeI(full.Width, full.Height);
        }

        var info = new SKImageInfo(scaled.Width, scaled.Height, SKColorType.Bgra8888, SKAlphaType.Premul);
        var decoded = new SKBitmap(info);
        var result = codec.GetPixels(info, decoded.GetPixels());
        if (result is not (SKCodecResult.Success or SKCodecResult.IncompleteInput))
        {
            decoded.Dispose();
            return null;
        }

        decoded = ApplyOrigin(decoded, codec.EncodedOrigin);
        var (finalWidth, finalHeight) = TargetDimensions(decoded.Width, decoded.Height, decodeWidth, decodeHeight);
        if (finalWidth == decoded.Width && finalHeight == decoded.Height)
        {
            return decoded;
        }

        var resized = decoded.Resize(
            new SKImageInfo(finalWidth, finalHeight, SKColorType.Bgra8888, SKAlphaType.Premul),
            new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.Linear));
        decoded.Dispose();
        return resized;
    }

    public static (int Width, int Height) TargetDimensions(int sourceWidth, int sourceHeight, int decodeWidth, int? decodeHeight)
    {
        if (decodeWidth <= 0 && decodeHeight is not > 0)
        {
            return (sourceWidth, sourceHeight);
        }

        var scaleW = decodeWidth > 0 ? (double)decodeWidth / sourceWidth : double.MaxValue;
        var scaleH = decodeHeight is > 0 ? (double)decodeHeight.Value / sourceHeight : double.MaxValue;
        var scale = Math.Min(1.0, Math.Min(scaleW, scaleH));
        return (Math.Max(1, (int)Math.Round(sourceWidth * scale)), Math.Max(1, (int)Math.Round(sourceHeight * scale)));
    }

    private static SKBitmap ApplyOrigin(SKBitmap bitmap, SKEncodedOrigin origin)
    {
        if (origin is SKEncodedOrigin.TopLeft or SKEncodedOrigin.Default)
        {
            return bitmap;
        }

        var swap = origin is SKEncodedOrigin.LeftTop or SKEncodedOrigin.RightTop or SKEncodedOrigin.RightBottom or SKEncodedOrigin.LeftBottom;
        var width = swap ? bitmap.Height : bitmap.Width;
        var height = swap ? bitmap.Width : bitmap.Height;
        var rotated = new SKBitmap(new SKImageInfo(width, height, SKColorType.Bgra8888, SKAlphaType.Premul));
        using (var canvas = new SKCanvas(rotated))
        {
            switch (origin)
            {
                case SKEncodedOrigin.TopRight:
                    canvas.Scale(-1, 1, width / 2f, 0);
                    break;
                case SKEncodedOrigin.BottomRight:
                    canvas.RotateDegrees(180, width / 2f, height / 2f);
                    break;
                case SKEncodedOrigin.BottomLeft:
                    canvas.Scale(1, -1, 0, height / 2f);
                    break;
                case SKEncodedOrigin.LeftTop:
                    canvas.Translate(width, 0);
                    canvas.RotateDegrees(90);
                    canvas.Scale(1, -1, 0, bitmap.Height / 2f);
                    break;
                case SKEncodedOrigin.RightTop:
                    canvas.Translate(width, 0);
                    canvas.RotateDegrees(90);
                    break;
                case SKEncodedOrigin.RightBottom:
                    canvas.Translate(0, height);
                    canvas.RotateDegrees(270);
                    canvas.Scale(1, -1, 0, bitmap.Height / 2f);
                    break;
                case SKEncodedOrigin.LeftBottom:
                    canvas.Translate(0, height);
                    canvas.RotateDegrees(270);
                    break;
            }

            canvas.DrawBitmap(bitmap, 0, 0);
        }

        bitmap.Dispose();
        return rotated;
    }
}

/// <summary>
/// The memory tier in front of the persistent derived-image cache (thumbnails, banner stills, cover
/// stills). Images are decoded off the UI thread, near the size they are displayed at, and kept in a
/// bounded least-recently-used set with in-flight de-duplication.
///
/// The key is the file identity (path + length + last write) plus the decode width, so a regenerated
/// derivative or a different display size never serves a stale image. The persistent cache and the
/// database stay the authorities; this only avoids decoding the same file twice. The image objects it
/// holds come from the installed <see cref="IDerivedImageDecoder"/>; the runtime treats them as opaque.
/// </summary>
public sealed class DerivedImageLoader : IDisposable
{
    public const int DefaultMaxEntries = 512;

    public const long DefaultMaxBytes = 96L * 1024 * 1024;

    private readonly int _maxEntries;
    private readonly long _maxBytes;
    private readonly IDerivedImageDecoder _decoder;
    private readonly SemaphoreSlim _decodeSlots;
    private readonly Lock _sync = new();
    private readonly Dictionary<ImageKey, LinkedListNode<Entry>> _entries = [];
    private readonly LinkedList<Entry> _recency = new();
    private readonly Dictionary<ImageKey, Task<Entry?>> _inflight = [];
    private readonly CancellationTokenSource _lifetime = new();
    private bool _disposed;
    private long _bytes;

    private long _hits;
    private long _misses;
    private long _decodes;
    private long _evictions;
    private long _failures;

    public DerivedImageLoader(
        int maxEntries = DefaultMaxEntries,
        long maxBytes = DefaultMaxBytes,
        int maxConcurrentDecodes = 2,
        IDerivedImageDecoder? decoder = null)
    {
        _maxEntries = Math.Max(1, maxEntries);
        _maxBytes = Math.Max(1, maxBytes);
        _decoder = decoder ?? SkiaDerivedImageDecoder.Instance;
        _decodeSlots = new SemaphoreSlim(Math.Max(1, maxConcurrentDecodes));
    }

    private static DerivedImageLoader _shared = new();

    /// <summary>The application-wide instance. The renderer replaces it once at startup with its own decoder.</summary>
    public static DerivedImageLoader Shared => Volatile.Read(ref _shared);

    public static void InstallShared(DerivedImageLoader loader)
    {
        ArgumentNullException.ThrowIfNull(loader);
        var previous = Interlocked.Exchange(ref _shared, loader);
        if (!ReferenceEquals(previous, loader))
        {
            previous.Dispose();
        }
    }

    public DerivedImageLoaderStats Stats
    {
        get
        {
            lock (_sync)
            {
                return new DerivedImageLoaderStats(
                    _entries.Count,
                    _bytes,
                    Interlocked.Read(ref _hits),
                    Interlocked.Read(ref _misses),
                    Interlocked.Read(ref _decodes),
                    Interlocked.Read(ref _evictions),
                    Interlocked.Read(ref _failures));
            }
        }
    }

    /// <summary>A synchronous memory-only lookup, so a warm surface can show an image in the same frame.</summary>
    public ImageLease? TryGetCached(string? path, int decodeWidth)
    {
        if (!TryCreateKey(path, decodeWidth, out var key))
        {
            return null;
        }

        lock (_sync)
        {
            if (_entries.TryGetValue(key, out var node))
            {
                _recency.Remove(node);
                _recency.AddFirst(node);
                Interlocked.Increment(ref _hits);
                return node.Value.Acquire();
            }
        }

        return null;
    }

    public ImageLease? TryGetCached(
        string resourceIdentity,
        string? path,
        int decodeWidth)
    {
        if (!TryCreateKey(resourceIdentity, path, decodeWidth, out var key))
        {
            return null;
        }

        lock (_sync)
        {
            if (_entries.TryGetValue(key, out var node))
            {
                _recency.Remove(node);
                _recency.AddFirst(node);
                Interlocked.Increment(ref _hits);
                return node.Value.Acquire();
            }
        }

        return null;
    }

    /// <summary>
    /// Returns the decoded image, decoding it in the background on a miss. A missing or corrupt file
    /// yields null (the surface keeps its placeholder); it never throws into the caller.
    /// </summary>
    public async Task<ImageLease?> LoadAsync(string? path, int decodeWidth, CancellationToken cancellationToken = default)
    {
        if (!TryCreateKey(path, decodeWidth, out var key))
        {
            return null;
        }

        Task<Entry?> pending;
        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_entries.TryGetValue(key, out var node))
            {
                _recency.Remove(node);
                _recency.AddFirst(node);
                Interlocked.Increment(ref _hits);
                return node.Value.Acquire();
            }

            if (_inflight.TryGetValue(key, out var inFlight))
            {
                // The shared decode has an application-owned lifetime. This caller only cancels its await.
                pending = inFlight;
            }
            else
            {
                Interlocked.Increment(ref _misses);
                var task = DecodeAndStoreAsync(key);
                _inflight[key] = task;
                pending = task;
            }
        }

        return (await pending.WaitAsync(cancellationToken).ConfigureAwait(false))?.Acquire();
    }

    public async Task<ImageLease?> LoadAsync(
        string resourceIdentity,
        string? path,
        int decodeWidth,
        CancellationToken cancellationToken = default)
    {
        if (!TryCreateKey(resourceIdentity, path, decodeWidth, out var key))
        {
            return null;
        }

        Task<Entry?> pending;
        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_entries.TryGetValue(key, out var node))
            {
                _recency.Remove(node);
                _recency.AddFirst(node);
                Interlocked.Increment(ref _hits);
                return node.Value.Acquire();
            }

            if (_inflight.TryGetValue(key, out var inFlight))
            {
                pending = inFlight;
            }
            else
            {
                Interlocked.Increment(ref _misses);
                pending = DecodeAndStoreAsync(key);
                _inflight[key] = pending;
            }
        }

        return (await pending.WaitAsync(cancellationToken).ConfigureAwait(false))?.Acquire();
    }

    public async Task PrefetchAsync(string? path, int decodeWidth, CancellationToken cancellationToken = default)
    {
        using var lease = await LoadAsync(path, decodeWidth, cancellationToken).ConfigureAwait(false);
    }

    public void Clear()
    {
        List<Entry> retired;
        lock (_sync)
        {
            retired = [.. _entries.Values.Select(node => node.Value)];
            _entries.Clear();
            _recency.Clear();
            _bytes = 0;
        }

        retired.ForEach(static entry => entry.Retire());
    }

    /// <summary>Drops cached decodes down to a fraction of the budget (memory pressure, minimise).</summary>
    public void Trim(double keepFraction)
    {
        var target = (long)(_maxBytes * Math.Clamp(keepFraction, 0, 1));
        var retired = new List<Entry>();
        lock (_sync)
        {
            while (_bytes > target && _recency.Last is { } last)
            {
                _recency.RemoveLast();
                _entries.Remove(last.Value.Key);
                _bytes -= last.Value.Bytes;
                retired.Add(last.Value);
                Interlocked.Increment(ref _evictions);
            }
        }
        retired.ForEach(static entry => entry.Retire());
    }

    private async Task<Entry?> DecodeAndStoreAsync(ImageKey key)
    {
        try
        {
            await _decodeSlots.WaitAsync(_lifetime.Token).ConfigureAwait(false);
            try
            {
                var image = await Task.Run(() => Decode(key), _lifetime.Token).ConfigureAwait(false);
                if (image is null)
                {
                    Interlocked.Increment(ref _failures);
                    return null;
                }

                Interlocked.Increment(ref _decodes);
                return Store(key, image);
            }
            finally
            {
                _decodeSlots.Release();
            }
        }
        catch (OperationCanceledException)
        {
            return null;
        }
        finally
        {
            lock (_sync)
            {
                _inflight.Remove(key);
            }
        }
    }

    private DecodedImage? Decode(ImageKey key)
    {
        using var measure = PerfTrace.Measure("image.decode", 150);
        try
        {
            return _decoder.Decode(key.Path, key.DecodeWidth);
        }
        catch (Exception exception) when (exception is IOException
            or UnauthorizedAccessException
            or NotSupportedException
            or ArgumentException
            or InvalidOperationException
            or System.Runtime.InteropServices.ExternalException)
        {
            // A corrupt or vanished derivative only costs a placeholder; the persistent cache
            // maintenance and the generating job own regeneration.
            Trace.TraceWarning("Derived image could not be decoded: {0}", exception.GetType().Name);
            return null;
        }
    }

    private Entry? Store(ImageKey key, DecodedImage image)
    {
        var bytes = Math.Max(1L, image.Bytes);
        if (bytes > _maxBytes)
        {
            DisposeResource(image.Image);
            Interlocked.Increment(ref _evictions);
            return null;
        }

        var retired = new List<Entry>();
        Entry nodeValue;
        lock (_sync)
        {
            if (_disposed)
            {
                DisposeResource(image.Image);
                return null;
            }

            if (_entries.TryGetValue(key, out var existing))
            {
                _recency.Remove(existing);
                _bytes -= existing.Value.Bytes;
                _entries.Remove(key);
                retired.Add(existing.Value);
            }

            nodeValue = new Entry(key, image.Image, bytes);
            var node = _recency.AddFirst(nodeValue);
            _entries[key] = node;
            _bytes += bytes;

            while ((_entries.Count > _maxEntries || _bytes > _maxBytes) && _recency.Last is { } last && last != node)
            {
                _recency.RemoveLast();
                _entries.Remove(last.Value.Key);
                _bytes -= last.Value.Bytes;
                retired.Add(last.Value);
                Interlocked.Increment(ref _evictions);
            }
        }
        retired.ForEach(static entry => entry.Retire());
        return nodeValue;
    }

    private static bool TryCreateKey(string? path, int decodeWidth, out ImageKey key) =>
        TryCreateKey(resourceIdentity: null, path, decodeWidth, out key);

    private static bool TryCreateKey(
        string? resourceIdentity,
        string? path,
        int decodeWidth,
        out ImageKey key)
    {
        key = default;
        if (string.IsNullOrWhiteSpace(path))
        {
            return false;
        }

        try
        {
            var fullPath = Path.GetFullPath(path);
            var identity = string.IsNullOrWhiteSpace(resourceIdentity)
                ? fullPath
                : resourceIdentity.Trim();
            key = new ImageKey(identity, fullPath, Math.Max(0, decodeWidth));
            return true;
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException)
        {
            return false;
        }
    }

    private readonly record struct ImageKey(string ResourceIdentity, string Path, int DecodeWidth);

    private static void DisposeResource(object resource)
    {
        if (resource is IDisposable disposable)
        {
            disposable.Dispose();
        }
    }

    public void Dispose()
    {
        lock (_sync)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
        }

        _lifetime.Cancel();
        Clear();
    }

    private sealed class Entry(ImageKey key, object image, long bytes)
    {
        private readonly Lock _sync = new();
        private int _leases;
        private bool _retired;

        public ImageKey Key { get; } = key;
        public object Image { get; } = image;
        public long Bytes { get; } = bytes;

        public ImageLease? Acquire()
        {
            lock (_sync)
            {
                if (_retired)
                {
                    return null;
                }

                _leases++;
                return new ImageLease(Image, Release);
            }
        }

        public void Retire()
        {
            lock (_sync)
            {
                if (_retired)
                {
                    return;
                }

                _retired = true;
                if (_leases == 0)
                {
                    DisposeResource(Image);
                }
            }
        }

        private void Release()
        {
            lock (_sync)
            {
                if (_leases > 0 && --_leases == 0 && _retired)
                {
                    DisposeResource(Image);
                }
            }
        }
    }
}

public sealed class ImageLease(object image, Action release) : IDisposable
{
    private Action? _release = release;
    public object Image { get; } = image;
    public void Dispose() => Interlocked.Exchange(ref _release, null)?.Invoke();
}

public readonly record struct DerivedImageLoaderStats(
    int Entries,
    long Bytes,
    long Hits,
    long Misses,
    long Decodes,
    long Evictions,
    long Failures);
