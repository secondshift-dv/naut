using System.Diagnostics;
using System.Text.Json;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;

namespace Neuterradise.App.Ui;

/// <summary>Bounded local measurements, enabled only by the explicitly isolated acceptance launch.</summary>
internal static class AcceptanceMeasurements
{
    public static void Observe(ProductRoot root, Window window, string isolatedAppState)
    {
        var samples = new Queue<object>();
        var output = Path.Combine(isolatedAppState, "acceptance-measurements.json");
        root.AddHandler(UIElement.PointerPressedEvent, new PointerEventHandler((_, args) =>
        {
            var timestamp = args.GetCurrentPoint(root).Timestamp;
            var age = Environment.TickCount64 - timestamp / 1000d;
            var started = Stopwatch.GetTimestamp();
            root.DispatcherQueue.TryEnqueue(() =>
            {
                var tiles = Descendants(root).OfType<MediaCardView>()
                    .Where(tile => tile.ActualWidth > 0 && tile.ActualHeight > 0)
                    .Take(100).Select(tile => new { WidthDip = tile.ActualWidth, HeightDip = tile.ActualHeight }).ToArray();
                samples.Enqueue(new
                {
                    AtUtc = DateTimeOffset.UtcNow,
                    NativeTimestampAvailable = timestamp > 0 && age is >= 0 and < 60000,
                    InputDispatchMs = timestamp > 0 && age is >= 0 and < 60000 ? (double?)age : null,
                    NextDispatchMs = Stopwatch.GetElapsedTime(started).TotalMilliseconds,
                    Scale = root.XamlRoot?.RasterizationScale,
                    Tiles = tiles,
                });
                while (samples.Count > 4096) samples.Dequeue();
            });
        }), handledEventsToo: true);
        window.Closed += (_, _) =>
        {
            try { File.WriteAllText(output, JsonSerializer.Serialize(samples)); }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            { Trace.TraceWarning("Isolated acceptance measurements could not be written."); }
        };
    }

    private static IEnumerable<DependencyObject> Descendants(DependencyObject parent)
    {
        var count = VisualTreeHelper.GetChildrenCount(parent);
        for (var i = 0; i < count; i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            yield return child;
            foreach (var descendant in Descendants(child)) yield return descendant;
        }
    }
}
