using System.Buffers;
using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.UI.Xaml;
using Neuterradise.App.Presentation;
using Neuterradise.App.Shell;
using Neuterradise.App.SystemServices.MediaTools;
using Neuterradise.App.SystemServices.Resources;
using SkiaSharp;
using Uno.WinUI.Graphics2DSK;
using Windows.Foundation;

namespace Neuterradise.App.Ui;

public enum VideoPurpose { Primary, Secondary, Hover, Backdrop }

/// <summary>
/// Composited prepared-video surface for Uno Skia Desktop. Frames are decoded by Naut's approved
/// FFmpeg artifact and drawn into the same Skia canvas as the rest of the UI, so Banner/hover video
/// never creates a native HWND or participates in Win32 airspace/z-order.
/// </summary>
public sealed class SkVideoView : SKCanvasElement, IDisposable
{
    private const int FramesPerSecond = 24;
    private const int BmpHeaderBytes = 54;
    private const int BmpRowBufferBytes = 64 * 1024;
    private const int MaximumEncodedFrameBytes = 8 * 1024 * 1024;
    private static readonly Lazy<string?> FfmpegExecutable = new(
        static () => ExternalToolResolver.ForProduction()
            .TryResolveExecutablePath(ExternalToolResolver.FfmpegToolId),
        LazyThreadSafetyMode.ExecutionAndPublication);
    private static int _prewarmStarted;
    private static int _instanceSeed;
    private static int _shutdown;
    private static readonly ConcurrentDictionary<int, WeakReference<SkVideoView>> Instances = new();

    private readonly int _instanceId;
    private static readonly Dictionary<PlaybackKey, SharedDecoder> Decoders = new(PlaybackKeyComparer.Instance);
    private SharedDecoder? _decoder;
    private static bool _reconciling;
    private static bool _reconcilePending;
    private static bool _stoppingAll;
    internal static int ActiveDecoderCount => Decoders.Values.Count(decoder => decoder.Running);
    public VideoPurpose Purpose { get; init; } = VideoPurpose.Primary;
    private SKBitmap? _frame;
    private bool _notifyFirstFrame;
    private readonly SKPaint _renderPaint = new() { IsAntialias = true };
    private string? _sourcePath;
    private MediaTransformState _presentation = MediaTransformState.Default;
    private bool _loop;
    private bool _still;
    private bool _active = true;
    private bool _disposed;

    public SkVideoView()
    {
        _instanceId = Interlocked.Increment(ref _instanceSeed);
        Instances[_instanceId] = new WeakReference<SkVideoView>(this);
        IsHitTestVisible = false;
        Loaded += (_, _) =>
        {
            EnsurePlayback();
            NotifyFrame();
        };
        Unloaded += (_, _) =>
        {
            // A shared hover surface can be re-parented in the same UI turn. Defer teardown so an
            // Unloaded event from the previous host cannot kill playback after the new host loads it.
            DispatcherQueue?.TryEnqueue(() =>
            {
                if (!_disposed && !IsLoaded)
                {
                    StopDecoder();
                }
            });
        };
    }

    /// <summary>
    /// Immediately quiesces every live composited-video surface in this process. App shutdown calls
    /// this before waiting on Vault/job durability so FFmpeg decode and video GPU work cannot keep
    /// running merely because the ordered data shutdown needs more time.
    /// </summary>
    public static int StopAllPlayback()
    {
        var stopped = 0;
        void StopCore()
        {
            _stoppingAll = true;
            try
            {
            foreach (var (id, weak) in Instances.ToArray())
            {
                if (!weak.TryGetTarget(out var view) || view._disposed)
                {
                    Instances.TryRemove(id, out _);
                    continue;
                }

                view.Stop();
                stopped++;
            }
            }
            finally { _stoppingAll = false; ReconcileDecoders(); }
        }

        if (UiDispatch.CheckAccess()) StopCore();
        else UiDispatch.InvokeAsync(StopCore).GetAwaiter().GetResult();
        return stopped;
    }

    /// <summary>Seals decoder admission before the application's durable shutdown starts.</summary>
    public static int QuiesceForShutdown()
    {
        Interlocked.Exchange(ref _shutdown, 1);
        return StopAllPlayback();
    }

    public static void PrewarmDecoder()
    {
        if (Interlocked.Exchange(ref _prewarmStarted, 1) != 0)
        {
            return;
        }

        TaskObserver.Observe(
            Task.Run(PrewarmDecoderCoreAsync),
            "SkVideoView.PrewarmDecoder");
    }

    private static async Task PrewarmDecoderCoreAsync()
    {
        var ffmpeg = FfmpegExecutable.Value;
        if (string.IsNullOrWhiteSpace(ffmpeg))
        {
            return;
        }

        using var process = new Process
        {
            StartInfo = new ProcessStartInfo(ffmpeg)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                RedirectStandardInput = false,
            },
        };

        process.StartInfo.ArgumentList.Add("-hide_banner");
        process.StartInfo.ArgumentList.Add("-loglevel");
        process.StartInfo.ArgumentList.Add("error");
        process.StartInfo.ArgumentList.Add("-nostdin");
        process.StartInfo.ArgumentList.Add("-f");
        process.StartInfo.ArgumentList.Add("lavfi");
        process.StartInfo.ArgumentList.Add("-i");
        process.StartInfo.ArgumentList.Add("color=c=black:s=16x16:d=0.04");
        process.StartInfo.ArgumentList.Add("-frames:v");
        process.StartInfo.ArgumentList.Add("1");
        process.StartInfo.ArgumentList.Add("-f");
        process.StartInfo.ArgumentList.Add("image2pipe");
        process.StartInfo.ArgumentList.Add("-vcodec");
        process.StartInfo.ArgumentList.Add("bmp");
        process.StartInfo.ArgumentList.Add("pipe:1");

        if (!process.Start())
        {
            return;
        }

        var outputDrain = process.StandardOutput.BaseStream.CopyToAsync(Stream.Null);
        var errorDrain = process.StandardError.ReadToEndAsync();
        await Task.WhenAll(outputDrain, process.WaitForExitAsync()).ConfigureAwait(false);
        await errorDrain.ConfigureAwait(false);
    }

    public event Action? FrameReady;

    public event Action? Ended;

    public event Action? Failed;

    public string? SourcePath => _sourcePath;
    public bool HasFrame => _frame is not null;

    public void SetPresentation(MediaTransformState presentation)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(presentation);
        if (_presentation == presentation)
        {
            return;
        }

        _presentation = presentation;
        Invalidate();
    }

    public (double X, double Y) FocalDeltaForDrag(double deltaX, double deltaY)
    {
        if (_frame is null || ActualWidth <= 0 || ActualHeight <= 0)
        {
            return (0, 0);
        }

        var placement = _presentation.Place(
            _frame.Width,
            _frame.Height,
            ActualWidth,
            ActualHeight);
        var overflowX = Math.Max(0, placement.Width - ActualWidth);
        var overflowY = Math.Max(0, placement.Height - ActualHeight);
        return (
            overflowX > 0.5 ? -deltaX / overflowX : 0,
            overflowY > 0.5 ? -deltaY / overflowY : 0);
    }

    public void Play(string? path, bool loop, bool still = false)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (Volatile.Read(ref _shutdown) != 0)
        {
            Stop();
            return;
        }
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            Stop();
            return;
        }

        var changed = !string.Equals(_sourcePath, path, StringComparison.OrdinalIgnoreCase);
        var modeChanged = _loop != loop || _still != still;
        _sourcePath = path;
        _loop = loop;
        _still = still;
        _active = true;
        if (changed)
        {
            StopDecoder();
        }
        else if (modeChanged) StopDecoder();

        EnsurePlayback();
    }

    public void SetActive(bool active)
    {
        if (_disposed || _active == active)
        {
            return;
        }

        _active = active;
        if (active)
        {
            EnsurePlayback();
        }
        else
        {
            StopDecoder();
        }
    }

    public void Stop()
    {
        if (_disposed)
        {
            return;
        }

        _active = false;
        _sourcePath = null;
        StopDecoder();
    }

    private void EnsurePlayback()
    {
        if (_disposed || Volatile.Read(ref _shutdown) != 0 || !_active || !IsLoaded || string.IsNullOrWhiteSpace(_sourcePath)
            || _decoder is not null) return;
        var ffmpeg = FfmpegExecutable.Value;
        if (string.IsNullOrWhiteSpace(ffmpeg))
        {
            Failed?.Invoke();
            return;
        }

        var key = new PlaybackKey(_sourcePath, _loop, _still);
        if (!Decoders.TryGetValue(key, out var decoder))
        {
            decoder = new SharedDecoder(key, ffmpeg);
            if (Decoders.Count == 0) ResourceGovernor.Shared.Changed += OnDecoderPolicyChanged;
            Decoders.Add(key, decoder);
        }
        _decoder = decoder;
        decoder.Attach(this);
        ReconcileDecoders();
    }

    private static void OnDecoderPolicyChanged(object? sender, EventArgs args) => UiDispatch.Run(ReconcileDecoders);

    private static void ReconcileDecoders()
    {
        _reconcilePending = true;
        if (_reconciling || _stoppingAll) return;
        _reconciling = true;
        try
        {
            while (_reconcilePending)
            {
                _reconcilePending = false;
                var governor = ResourceGovernor.Shared;
                var capacity = Volatile.Read(ref _shutdown) != 0 || governor.IsMemoryPressure ? 0
                    : governor.Tier == PresentationTier.Full ? 2 : governor.Tier == PresentationTier.Reduced ? 1 : 0;
                var ordered = Decoders.Values.Where(decoder => !decoder.Completed && !decoder.Retired
                    && (decoder.Priority != VideoPurpose.Hover || governor.IsHoverVideoAllowed)
                    && (decoder.Priority is VideoPurpose.Primary or VideoPurpose.Hover || decoder.Still || governor.Tier == PresentationTier.Full))
                    .OrderBy(decoder => decoder.Priority).ThenByDescending(decoder => decoder.Running).Take(capacity).ToArray();
                foreach (var decoder in Decoders.Values.ToArray())
                    if (decoder.Running && !ordered.Contains(decoder)) decoder.Suspend();
                foreach (var decoder in ordered) decoder.Start();
            }
        }
        finally { _reconciling = false; }
    }

    private readonly record struct PlaybackKey(string Path, bool Loop, bool Still);
    private sealed class PlaybackKeyComparer : IEqualityComparer<PlaybackKey>
    {
        public static PlaybackKeyComparer Instance { get; } = new();
        public bool Equals(PlaybackKey a, PlaybackKey b) =>
            a.Loop == b.Loop && a.Still == b.Still && StringComparer.OrdinalIgnoreCase.Equals(a.Path, b.Path);
        public int GetHashCode(PlaybackKey key) => HashCode.Combine(StringComparer.OrdinalIgnoreCase.GetHashCode(key.Path), key.Loop, key.Still);
    }

    // The UI thread owns the decoder registry, membership and published frame. Decode workers
    // exclusively own the next frame and recycle the previous frame only after every view is updated.
    private sealed class SharedDecoder(PlaybackKey key, string ffmpeg)
    {
        private readonly object _processLock = new();
        private SkVideoView[] _views = [];
        private CancellationTokenSource? _cancellation;
        private Process? _process;
        private SKBitmap? _current;
        private bool _retired;
        private bool _running;
        private bool _completed;
        private int _epoch;
        public bool Running => _running;
        public bool Still => key.Still;
        public bool Completed => _completed;
        public bool Retired => _retired;
        public VideoPurpose Priority => _views.Length == 0 ? VideoPurpose.Backdrop : _views.Min(view => view.Purpose);

        public void Attach(SkVideoView view)
        {
            _views = [.. _views, view];
            if (_current is not null)
            {
                view.AcceptFrame(this, _current);
                view.NotifyFrame();
            }
        }

        public void Start()
        {
            if (_running || _retired || _completed) return;
            _running = true;
            var epoch = ++_epoch;
            var cancellation = new CancellationTokenSource();
            if (key.Still) cancellation.CancelAfter(TimeSpan.FromSeconds(15));
            _cancellation = cancellation;
            TaskObserver.Observe(RunAsync(epoch, cancellation.Token), "SkVideoView.SharedDecoder");
        }

        public void Detach(SkVideoView view)
        {
            _views = _views.Where(candidate => !ReferenceEquals(candidate, view)).ToArray();
            if (_views.Length != 0)
            {
                ReconcileDecoders();
                return;
            }
            Decoders.Remove(key);
            if (Decoders.Count == 0) ResourceGovernor.Shared.Changed -= OnDecoderPolicyChanged;
            _retired = true;
            Suspend();
            ReconcileDecoders();
        }

        public void Suspend()
        {
            ++_epoch;
            _running = false;
            var cancellation = _cancellation;
            _cancellation = null;
            cancellation?.Cancel();
            cancellation?.Dispose();
            Process? process;
            lock (_processLock)
            {
                process = _process;
                _process = null;
            }
            if (process is not null) TryKill(process);
            foreach (var view in _views)
            {
                if (!ReferenceEquals(view._decoder, this)) continue;
                view._frame = null;
                view._notifyFirstFrame = false;
                view.Invalidate();
            }
            _current?.Dispose();
            _current = null;
        }

        private async Task RunAsync(int epoch, CancellationToken cancellationToken)
        {
            var failed = false;
            try
            {
                await DecodeAsync(ffmpeg, epoch, cancellationToken).ConfigureAwait(false);
                failed = cancellationToken.IsCancellationRequested;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                failed = true;
            }
            catch (Exception exception)
            {
                failed = true;
                Trace.TraceError("Shared video decoder failed: {0}", exception);
            }
            finally
            {
                // A deadline cancels the current epoch; suspension/detachment first retires it.
                // Reconcile only after the worker has released its process and spare frame.
                await UiDispatch.InvokeAsync(() => Complete(epoch, failed)).ConfigureAwait(false);
            }
        }

        private void Complete(int epoch, bool failed)
        {
            if (_retired || epoch != _epoch) return;
            _running = false;
            _completed = true;
            _cancellation?.Dispose();
            _cancellation = null;
            try
            {
                foreach (var view in _views)
                    if (ReferenceEquals(view._decoder, this))
                    {
                        if (failed) view.Failed?.Invoke();
                        else view.Ended?.Invoke();
                    }
            }
            finally { ReconcileDecoders(); }
        }

        private async Task DecodeAsync(string ffmpegPath, int epoch, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (Volatile.Read(ref _shutdown) != 0) return;
            using var process = new Process
            {
                StartInfo = CreateStartInfo(ffmpegPath, key.Path, key.Loop, key.Still),
                EnableRaisingEvents = false,
            };
            if (!process.Start()) throw new InvalidOperationException("FFmpeg video decoder did not start.");
            lock (_processLock)
            {
                if (cancellationToken.IsCancellationRequested || epoch != Volatile.Read(ref _epoch))
                {
                    TryKill(process);
                    return;
                }
                _process = process;
            }
            Task? errorDrain = null;
            var playbackClock = new Stopwatch();
            long frameIndex = 0;
            SKBitmap? spare = null;
            try
            {
                // Closing the owned process also unblocks a redirected pipe read that
                // does not promptly honor cancellation. The worker owns the bounded join.
                using var cancellationRegistration = cancellationToken.Register(() => TryKill(process, waitForExit: false));
                errorDrain = process.StandardError.BaseStream.CopyToAsync(Stream.Null);
                var header = new byte[BmpHeaderBytes];
                var rowBuffer = new byte[BmpRowBufferBytes];
                while (!cancellationToken.IsCancellationRequested)
                {
                    var bitmap = await ReadBmpFrameAsync(process.StandardOutput.BaseStream, header, rowBuffer, cancellationToken, spare)
                        .ConfigureAwait(false);
                    spare = null;
                    if (bitmap is null) break;
                    var accepted = false;
                    try
                    {
                        if (frameIndex == 0) playbackClock.Start();
                        else
                        {
                            var delay = TimeSpan.FromSeconds(frameIndex / (double)FramesPerSecond) - playbackClock.Elapsed;
                            if (delay > TimeSpan.Zero) await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
                            else if (delay < TimeSpan.FromMilliseconds(-250))
                            {
                                playbackClock.Restart();
                                frameIndex = 0;
                            }
                        }
                        frameIndex++;
                        await UiDispatch.InvokeAsync(() =>
                        {
                            if (_retired || epoch != _epoch || cancellationToken.IsCancellationRequested) return;
                            spare = _current;
                            _current = bitmap;
                            accepted = true;
                            var views = _views;
                            foreach (var view in views) view.AcceptFrame(this, bitmap);
                            foreach (var view in views)
                                if (ReferenceEquals(view._decoder, this)) view.NotifyFrame();
                        }).ConfigureAwait(false);
                    }
                    finally { if (!accepted) bitmap.Dispose(); }
                }
                if (!cancellationToken.IsCancellationRequested)
                {
                    await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
                    if (process.ExitCode != 0)
                        throw new InvalidOperationException($"FFmpeg video decoder exited with code {process.ExitCode}.");
                    if (key.Loop && !key.Still)
                        throw new InvalidOperationException("Looping FFmpeg video decoder ended unexpectedly.");
                }
            }
            finally
            {
                spare?.Dispose();
                lock (_processLock)
                {
                    if (ReferenceEquals(_process, process)) _process = null;
                }
                TryKill(process);
                if (errorDrain is not null)
                {
                    try { await errorDrain.ConfigureAwait(false); }
                    catch { /* Process teardown can close stderr during the drain. */ }
                }
            }
        }
    }

    private void AcceptFrame(SharedDecoder decoder, SKBitmap bitmap)
    {
        if (_disposed || !ReferenceEquals(_decoder, decoder)) return;
        _notifyFirstFrame |= _frame is null;
        _frame = bitmap;
    }

    private void NotifyFrame()
    {
        if (_disposed || !_active || !IsLoaded || _frame is null) return;
        Invalidate();
        if (!_notifyFirstFrame) return;
        _notifyFirstFrame = false;
        FrameReady?.Invoke();
    }

    private static ProcessStartInfo CreateStartInfo(string ffmpegPath, string sourcePath, bool loop, bool still)
    {
        var startInfo = new ProcessStartInfo(ffmpegPath)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = false,
        };

        startInfo.ArgumentList.Add("-hide_banner");
        startInfo.ArgumentList.Add("-loglevel");
        startInfo.ArgumentList.Add("error");
        startInfo.ArgumentList.Add("-nostdin");
        foreach (var argument in new[] { "-max_alloc", "67108864", "-threads", "1", "-protocol_whitelist", "file", "-format_whitelist", "mov,matroska,webm" })
            startInfo.ArgumentList.Add(argument);
        if (loop && !still)
        {
            startInfo.ArgumentList.Add("-stream_loop");
            startInfo.ArgumentList.Add("-1");
        }

        startInfo.ArgumentList.Add("-i");
        startInfo.ArgumentList.Add(sourcePath);
        foreach (var argument in new[] { "-map", "0:v:0", "-sn", "-dn", "-threads", "1", "-filter_threads", "1" })
            startInfo.ArgumentList.Add(argument);
        startInfo.ArgumentList.Add("-an");
        startInfo.ArgumentList.Add("-vf");
        startInfo.ArgumentList.Add($"fps={FramesPerSecond},scale=w='if(gte(iw,ih),min(1600,iw),min(900,iw))':h='if(gte(iw,ih),min(900,ih),min(1600,ih))':force_original_aspect_ratio=decrease");
        if (still)
        {
            startInfo.ArgumentList.Add("-frames:v");
            startInfo.ArgumentList.Add("1");
        }
        // Opaque BGRA frames permit a bounded row copy instead of decoding a BMP into
        // a second pixel buffer on every video frame.
        startInfo.ArgumentList.Add("-pix_fmt");
        startInfo.ArgumentList.Add("bgra");
        startInfo.ArgumentList.Add("-f");
        startInfo.ArgumentList.Add("image2pipe");
        startInfo.ArgumentList.Add("-vcodec");
        startInfo.ArgumentList.Add("bmp");
        startInfo.ArgumentList.Add("pipe:1");
        return startInfo;
    }

    private static async Task<SKBitmap?> ReadBmpFrameAsync(
        Stream stream, byte[] header, byte[] rowBuffer, CancellationToken cancellationToken, SKBitmap? reusable)
    {
        if (!await ReadExactlyOrEofAsync(stream, header, cancellationToken).ConfigureAwait(false))
        {
            reusable?.Dispose();
            return null;
        }

        if (header[0] != (byte)'B' || header[1] != (byte)'M')
        {
            throw new InvalidDataException("FFmpeg returned a non-BMP frame.");
        }

        var frameLength = BinaryPrimitives.ReadInt32LittleEndian(header.AsSpan(2, 4));
        if (frameLength < header.Length || frameLength > MaximumEncodedFrameBytes)
        {
            throw new InvalidDataException($"FFmpeg returned an invalid BMP frame length: {frameLength}.");
        }

        var width = BinaryPrimitives.ReadInt32LittleEndian(header.AsSpan(18, 4));
        var rawHeight = BinaryPrimitives.ReadInt32LittleEndian(header.AsSpan(22, 4));
        var bits = BinaryPrimitives.ReadUInt16LittleEndian(header.AsSpan(28, 2));
        var pixelOffset = BinaryPrimitives.ReadInt32LittleEndian(header.AsSpan(10, 4));
        var height = Math.Abs((long)rawHeight);
        var stride = ((long)width * bits + 31) / 32 * 4;
        if (width <= 0 || height <= 0 || Math.Max(width, height) > 1600 || Math.Min(width, height) > 900
            || bits is not (24 or 32) || pixelOffset < header.Length || pixelOffset > 138
            || frameLength != pixelOffset + stride * height)
            throw new InvalidDataException("FFmpeg returned an invalid or oversized BMP geometry.");

        // FFmpeg emits opaque, uncompressed BGRA BMPs. Read batches of bottom-up rows
        // directly into the reusable bitmap with bounded scratch space per decoder.
        var compression = BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(30, 4));
        if (bits == 32 && pixelOffset == header.Length && compression == 0 && rawHeight > 0)
        {
            var info = new SKImageInfo(width, checked((int)height), SKColorType.Bgra8888, SKAlphaType.Opaque);
            var bitmap = reusable is not null && reusable.Info == info
                ? reusable
                : new SKBitmap(info);
            try
            {
                var rowBytes = checked(width * 4);
                var rowsPerBatch = rowBuffer.Length / rowBytes;
                for (var inputRow = 0; inputRow < info.Height;)
                {
                    var rows = Math.Min(rowsPerBatch, info.Height - inputRow);
                    await ReadExactlyAsync(stream, rowBuffer.AsMemory(0, rows * rowBytes), cancellationToken)
                        .ConfigureAwait(false);
                    var pixels = bitmap.GetPixelSpan();
                    for (var row = 0; row < rows; row++)
                    {
                        var destinationRow = info.Height - inputRow - row - 1;
                        rowBuffer.AsSpan(row * rowBytes, rowBytes)
                            .CopyTo(pixels.Slice(destinationRow * bitmap.RowBytes, rowBytes));
                    }
                    inputRow += rows;
                }

                bitmap.NotifyPixelsChanged();
                if (!ReferenceEquals(bitmap, reusable)) reusable?.Dispose();
                return bitmap;
            }
            catch
            {
                if (!ReferenceEquals(bitmap, reusable)) bitmap.Dispose();
                throw;
            }
        }

        var buffer = ArrayPool<byte>.Shared.Rent(frameLength);
        try
        {
            Buffer.BlockCopy(header, 0, buffer, 0, header.Length);
            await ReadExactlyAsync(
                    stream,
                    buffer.AsMemory(header.Length, frameLength - header.Length),
                    cancellationToken)
                .ConfigureAwait(false);

            using var encoded = new MemoryStream(buffer, 0, frameLength, writable: false, publiclyVisible: true);
            var bitmap = SKBitmap.Decode(encoded)
                ?? throw new InvalidDataException("FFmpeg returned a BMP frame that Skia could not decode.");
            reusable?.Dispose();
            return bitmap;
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    private static async Task<bool> ReadExactlyOrEofAsync(
        Stream stream,
        byte[] buffer,
        CancellationToken cancellationToken)
    {
        var read = 0;
        while (read < buffer.Length)
        {
            var count = await stream.ReadAsync(buffer.AsMemory(read), cancellationToken).ConfigureAwait(false);
            if (count == 0)
            {
                if (read == 0)
                {
                    return false;
                }

                throw new EndOfStreamException("FFmpeg ended in the middle of a video frame.");
            }

            read += count;
        }

        return true;
    }

    private static async Task ReadExactlyAsync(
        Stream stream,
        Memory<byte> buffer,
        CancellationToken cancellationToken)
    {
        var read = 0;
        while (read < buffer.Length)
        {
            var count = await stream.ReadAsync(buffer[read..], cancellationToken).ConfigureAwait(false);
            if (count == 0)
            {
                throw new EndOfStreamException("FFmpeg ended in the middle of a video frame.");
            }

            read += count;
        }
    }

    private void StopDecoder()
    {
        var decoder = _decoder;
        _decoder = null;
        // A borrowed frame must never outlive its decoder lease.
        var hadFrame = _frame is not null;
        _frame = null;
        _notifyFirstFrame = false;
        decoder?.Detach(this);
        if (hadFrame) Invalidate();
    }

    private static void TryKill(Process process, bool waitForExit = true)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                // Kill is asynchronous on Windows. Briefly join here so shutdown/packaging does
                // not race an FFmpeg executable that still owns its image or source handles.
                if (waitForExit) process.WaitForExit(750);
            }
        }
        catch (InvalidOperationException)
        {
        }
        catch (System.ComponentModel.Win32Exception)
        {
        }
        catch (COMException exception) when (exception.HResult == unchecked((int)0x80070006))
        {
            // E_HANDLE: async FFmpeg exit/disposal raced the bounded join after Kill.
        }
    }

    protected override void RenderOverride(SKCanvas canvas, Size area)
    {
        if (_frame is null || area.Width <= 0 || area.Height <= 0)
        {
            return;
        }

        var placement = _presentation.Place(
            _frame.Width,
            _frame.Height,
            area.Width,
            area.Height);
        var destination = new SKRect(
            (float)placement.TranslateX,
            (float)placement.TranslateY,
            (float)(placement.TranslateX + placement.Width),
            (float)(placement.TranslateY + placement.Height));

        canvas.Save();
        canvas.ClipRect(new SKRect(0, 0, (float)area.Width, (float)area.Height));
        if (placement.Rotation != 0) canvas.RotateDegrees((float)placement.Rotation, (float)area.Width / 2, (float)area.Height / 2);
        canvas.DrawBitmap(_frame, destination, _renderPaint);
        canvas.Restore();
    }

    public new void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        Instances.TryRemove(_instanceId, out _);
        _active = false;
        _sourcePath = null;
        StopDecoder();
        _renderPaint.Dispose();
    }
}
