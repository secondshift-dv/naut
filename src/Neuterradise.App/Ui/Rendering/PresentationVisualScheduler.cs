using Neuterradise.App.Presentation;
using Microsoft.UI.Xaml.Media;

namespace Neuterradise.App.Ui;

internal static class PresentationVisualScheduler
{
    private static readonly Dictionary<BackdropView, long> Hosts = [];
    private static long _sequence;
    private static bool _updating;
    private static bool _dirty;

    private static readonly List<SceneMotionView> SceneHosts = [];
    private const int MaxActiveSceneLayers = 8;

    public static void SetSceneActive(SceneMotionView host, bool active)
    {
        if (active)
        {
            if (SceneHosts.Contains(host)) return;
            SceneHosts.Add(host);
        }
        else
        {
            if (!SceneHosts.Remove(host)) return;
            host.ApplyAdmission(false);
        }
        for (var index = 0; index < SceneHosts.Count; index++)
            SceneHosts[index].ApplyAdmission(index < MaxActiveSceneLayers);
    }

    private static event Action<long>? Frame;
    public static void StartClock(Action<long> handler)
    {
        if (Frame is null) CompositionTarget.Rendering += OnFrame;
        Frame += handler;
    }

    public static void StopClock(Action<long> handler)
    {
        Frame -= handler;
        if (Frame is null) CompositionTarget.Rendering -= OnFrame;
    }

    private static void OnFrame(object? sender, object args) => Frame?.Invoke(Environment.TickCount64);

    public static void Update(BackdropView host, bool active)
    {
        if (active) Hosts.TryAdd(host, ++_sequence);
        else Hosts.Remove(host);
        _dirty = true;
        if (_updating) return;
        _updating = true;
        try
        {
            while (_dirty)
            {
                _dirty = false;
                var previewRank = 0;
                var rank = 0;
                foreach (var entry in Hosts.OrderBy(entry => entry.Key.VisualPriority).ThenBy(entry => entry.Value).ToArray())
                {
                    if (!Hosts.ContainsKey(entry.Key)) continue;
                    var priority = entry.Key.VisualPriority;
                    entry.Key.ApplyScheduledTier(PresentationVisualPolicy.Quality(priority, rank++, previewRank));
                    if ((int)priority >= (int)PresentationVisualPriority.SelectedPreview) previewRank++;
                }
            }
        }
        finally { _updating = false; }
    }
}
