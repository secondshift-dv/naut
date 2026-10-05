using Neuterradise.App.Presentation;

namespace Neuterradise.App.Ui;

/// <summary>
/// Runtime normalization for Home rails. Vault health and the monthly chronicle are product-level
/// Home affordances, so presentation packs may position/style them but cannot remove them.
/// </summary>
internal static class HomeRailPolicy
{
    public const string StatisticsId = "statistics";
    public const string ActivityId = "activity";

    public static IReadOnlyList<HomeRailPlan> Resolve(HomeLayoutPlan layout)
    {
        ArgumentNullException.ThrowIfNull(layout);

        var rails = new List<HomeRailPlan>(layout.Rails.Count + 2);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var rail in layout.Rails.Where(static rail => rail.Visible))
        {
            if (seen.Add(rail.Id))
            {
                rails.Add(rail);
            }
        }

        var fallbackStyle = layout.HeroPlacement switch
        {
            "inset" => "tile",
            "split" => "compact",
            _ => "strip",
        };

        if (seen.Add(StatisticsId))
        {
            rails.Add(new HomeRailPlan(StatisticsId, true, fallbackStyle, 4));
        }

        if (seen.Add(ActivityId))
        {
            rails.Add(new HomeRailPlan(ActivityId, true, fallbackStyle, 8));
        }

        return rails;
    }
}
