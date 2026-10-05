using System.Buffers;
using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Diagnostics;
using Microsoft.UI.Xaml;
using Neuterradise.App.Presentation;
using Neuterradise.App.Shell;
using Neuterradise.App.SystemServices.MediaTools;
using SkiaSharp;
using Uno.WinUI.Graphics2DSK;
using Windows.Foundation;

namespace Neuterradise.App.Ui;

/// <summary>
/// Composited prepared-video surface for Uno Skia Desktop. Frames are decoded by Naut's approved
/// FFmpeg artifact and drawn into the same Skia canvas as the rest of the UI, so Banner/hover video
/// never creates a native HWND or participates in Win32 airspace/z-order.
/// </summary>
public sealed class SkVideoView : SKCanvasElement, IDisposable
{
    private const int FramesPerSecond = 24;
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
    private readonly object _processLock = new();
    private CancellationTokenSource? _decodeCancellation;
    private Process? _process;
    private SKBitmap? _frame;
    private string? _sourcePath;
    private MediaTransformState _presentation = MediaTransformState.Default;
    private bool _loop;
    private bool _active = true;
    private bool _disposed;
    private int _generation;

    public SkVideoView()
    {
        _instanceId = Interlocked.Increment(ref _instanceSeed);
        Instances[_instanceId] = new WeakReference<SkVideoView>(this);
        IsHitTestVisible = false;
        Loaded += (_, _) => EnsurePlayback();
        Unloaded += (_, _) =>
        {
            // A shared hover surface can be re-parented in the same UI turn. Defer teardown so an
            // Unloaded event from the previous host cannot kill playback after the new host loads it.
            DispatcherQueue?.TryEnqueue(() =>
            {
                if (!_disposed && !IsLoaded)
                {
                    StopDecoder(clearFrame: true);
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

    public void Play(string? path, bool loop)
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

        var changed = !string.Equals(_sourcePath, path, StringComparison.OrdinalIgnoreCase) || _loop != loop;
        _sourcePath = path;
        _loop = loop;
        _active = true;
        if (changed)
        {
            StopDecoder(clearFrame: true);
        }

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
            StopDecoder(clearFrame: false);
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
        StopDecoder(clearFrame: true);
    }

    private void EnsurePlayback()
    {
        if (_disposed || Volatile.Read(ref _shutdown) != 0 || !_active || !IsLoaded || string.IsNullOrWhiteSpace(_sourcePath))
        {
            return;
        }

        lock (_processLock)
        {
            if (_process is not null)
            {
                return;
            }
        }

        var ffmpeg = FfmpegExecutable.Value;
        if (string.IsNullOrWhiteSpace(ffmpeg))
        {
            Failed?.Invoke();
            return;
        }

        var generation = ++_generation;
        var cancellation = new CancellationTokenSource();
        _decodeCancellation?.Dispose();
        _decodeCancellation = cancellation;
        TaskObserver.Observe(
            DecodeAsync(ffmpeg, _sourcePath, _loop, generation, cancellation.Token),
            "SkVideoView.DecodeAsync",
            _ => UiDispatch.Run(() =>
            {
                if (!_disposed && generation == _generation)
                {
                    Failed?.Invoke();
                }
            }));
    }

    private async Task DecodeAsync(
        string ffmpegPath,
        string sourcePath,
        bool loop,
        int generation,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (Volatile.Read(ref _shutdown) != 0) return;
        using var process = new Process
        {
            StartInfo = CreateStartInfo(ffmpegPath, sourcePath, loop),
            EnableRaisingEvents = false,
        };

        if (!process.Start())
        {
            throw new InvalidOperationException("FFmpeg video decoder did not start.");
        }

        lock (_processLock)
        {
            if (generation != _generation || cancellationToken.IsCancellationRequested)
            {
                TryKill(process);
                return;
            }

            _process = process;
        }

        var errorDrain = process.StandardError.ReadToEndAsync();
        var playbackClock = Stopwatch.StartNew();
        long frameIndex = 0;
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                var bitmap = await ReadBmpFrameAsync(process.StandardOutput.BaseStream, cancellationToken)
                    .ConfigureAwait(false);
                if (bitmap is null)
                {
                    break;
                }

                if (frameIndex > 0)
                {
                    var due = TimeSpan.FromSeconds(frameIndex / (double)FramesPerSecond);
                    var delay = due - playbackClock.Elapsed;
                    if (delay > TimeSpan.Zero)
                    {
                        await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
                    }
                }
                frameIndex++;

                var accepted = false;
                await UiDispatch.InvokeAsync(() =>
                {
                    if (!_disposed
                        && generation == _generation
                        && _active
                        && IsLoaded
                        && string.Equals(_sourcePath, sourcePath, StringComparison.OrdinalIgnoreCase))
                    {
                        var previous = _frame;
                        _frame = bitmap;
                        accepted = true;
                        Invalidate();
                        previous?.Dispose();
                        if (previous is null)
                        {
                            FrameReady?.Invoke();
                        }
                    }
                }).ConfigureAwait(false);

                if (!accepted)
                {
                    bitmap.Dispose();
                }
            }

            if (!cancellationToken.IsCancellationRequested)
            {
                await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
                if (process.ExitCode != 0)
                {
                    throw new InvalidOperationException($"FFmpeg video decoder exited with code {process.ExitCode}.");
                }

                if (loop)
                {
                    throw new InvalidOperationException("Looping FFmpeg video decoder ended unexpectedly.");
                }

                UiDispatch.Run(() =>
                {
                    if (!_disposed && generation == _generation)
                    {
                        Ended?.Invoke();
                    }
                });
            }
        }
        finally
        {
            lock (_processLock)
            {
                if (ReferenceEquals(_process, process))
                {
                    _process = null;
                }
            }

            if (!process.HasExited)
            {
                TryKill(process);
            }

            try
            {
                await errorDrain.ConfigureAwait(false);
            }
            catch
            {
                // Process teardown can close stderr while the drain is pending.
            }
        }
    }

    private static ProcessStartInfo CreateStartInfo(string ffmpegPath, string sourcePath, bool loop)
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
        if (loop)
        {
            startInfo.ArgumentList.Add("-stream_loop");
            startInfo.ArgumentList.Add("-1");
        }

        startInfo.ArgumentList.Add("-i");
        startInfo.ArgumentList.Add(sourcePath);
        startInfo.ArgumentList.Add("-an");
        startInfo.ArgumentList.Add("-vf");
        startInfo.ArgumentList.Add($"fps={FramesPerSecond}");
        startInfo.ArgumentList.Add("-f");
        startInfo.ArgumentList.Add("image2pipe");
        startInfo.ArgumentList.Add("-vcodec");
        startInfo.ArgumentList.Add("bmp");
        startInfo.ArgumentList.Add("pipe:1");
        return startInfo;
    }

    private static async Task<SKBitmap?> ReadBmpFrameAsync(Stream stream, CancellationToken cancellationToken)
    {
        var header = new byte[14];
        if (!await ReadExactlyOrEofAsync(stream, header, cancellationToken).ConfigureAwait(false))
        {
            return null;
        }

        if (header[0] != (byte)'B' || header[1] != (byte)'M')
        {
            throw new InvalidDataException("FFmpeg returned a non-BMP frame.");
        }

        var frameLength = BinaryPrimitives.ReadInt32LittleEndian(header.AsSpan(2, 4));
        if (frameLength < 54 || frameLength > MaximumEncodedFrameBytes)
        {
            throw new InvalidDataException($"FFmpeg returned an invalid BMP frame length: {frameLength}.");
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
            return SKBitmap.Decode(encoded)
                ?? throw new InvalidDataException("FFmpeg returned a BMP frame that Skia could not decode.");
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

    private void StopDecoder(bool clearFrame)
    {
        ++_generation;
        var cancellation = _decodeCancellation;
        _decodeCancellation = null;
        cancellation?.Cancel();
        cancellation?.Dispose();

        Process? process;
        lock (_processLock)
        {
            process = _process;
            _process = null;
        }

        if (process is not null)
        {
            TryKill(process);
        }

        if (clearFrame)
        {
            var previous = _frame;
            _frame = null;
            previous?.Dispose();
            Invalidate();
        }
    }

    private static void TryKill(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                // Kill is asynchronous on Windows. Briefly join here so shutdown/packaging does
                // not race an FFmpeg executable that still owns its image or source handles.
                process.WaitForExit(750);
            }
        }
        catch (InvalidOperationException)
        {
        }
        catch (System.ComponentModel.Win32Exception)
        {
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
        using var paint = new SKPaint { IsAntialias = true };
        canvas.DrawBitmap(_frame, destination, paint);
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
        StopDecoder(clearFrame: true);
    }
}
