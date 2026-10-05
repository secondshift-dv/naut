using Neuterradise.App.Presentation;

namespace Neuterradise.App.Ui;

internal static class PresentationVisualScheduler
{
    private static readonly Dictionary<BackdropView, long> Hosts = [];
    private static long _sequence;
    private static bool _updating;
    private static bool _dirty;

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
