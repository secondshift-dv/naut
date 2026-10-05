using Microsoft.UI.Xaml.Controls;
using Neuterradise.App.Media;
using Neuterradise.App.Presentation;

namespace Neuterradise.App.Ui;

/// <summary>
/// One transient prepared-video surface shared by hover consumers. Playback is composited through
/// <see cref="SkVideoView"/>, never through a native Win32 video presenter.
/// </summary>
public sealed class HoverVideoCoordinator
{
    private static HoverVideoCoordinator? _shared;

    private readonly SkVideoView _surface = new();
    private Panel? _host;
    private string? _path;
    private IMotionLease? _lease;

    private HoverVideoCoordinator()
    {
        _surface.Ended += StopAll;
        _surface.Failed += StopAll;
    }

    public static HoverVideoCoordinator Shared => _shared ??= new HoverVideoCoordinator();

    public void Play(Panel host, string? videoPath, MediaTransformState? presentation = null)
    {
        if (string.IsNullOrWhiteSpace(videoPath))
        {
            Release(host);
            return;
        }

        if (ReferenceEquals(_host, host)
            && string.Equals(_path, videoPath, StringComparison.OrdinalIgnoreCase))
        {
            _surface.SetPresentation(presentation ?? MediaTransformState.Default);
            return;
        }

        StopAll();

        var lease = HoverPlaybackArbiter.Instance.TryAcquire(
            Guid.Empty,
            "shared-hover",
            onEvicted: () => UiDispatch.Run(StopAfterEviction));
        if (lease is null)
        {
            return;
        }

        _lease = lease;
        _host = host;
        _path = videoPath;
        _surface.SetPresentation(presentation ?? MediaTransformState.Default);

        // Seed source/play intent before the surface enters the visual tree. Then its first Loaded
        // callback can start decoding immediately instead of requiring a second hover cycle.
        _surface.Play(videoPath, loop: false);

        host.Children.Add(_surface);
    }

    public void Release(Panel host)
    {
        if (ReferenceEquals(_host, host))
        {
            StopAll();
        }
    }

    public void NotifyScroll()
    {
        HoverPlaybackArbiter.Instance.NotifyScroll();
        StopAll();
    }

    public void StopAll()
    {
        var lease = _lease;
        _lease = null;
        lease?.Release();
        StopPlayback();
    }

    private void StopAfterEviction()
    {
        _lease = null;
        StopPlayback();
    }

    private void StopPlayback()
    {
        _surface.Stop();
        _path = null;

        if (_host is not null)
        {
            _host.Children.Remove(_surface);
            _host = null;
        }
    }
}
