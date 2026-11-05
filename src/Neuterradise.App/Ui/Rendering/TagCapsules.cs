using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Media;
using Neuterradise.App.Profiles;

namespace Neuterradise.App.Ui;

public static class TagCapsules
{
    private static readonly (byte R, byte G, byte B)[] Palette =
        [(61, 214, 250), (180, 122, 255), (56, 231, 159), (255, 148, 83),
         (255, 99, 160), (255, 209, 68), (111, 159, 255), (255, 112, 105),
         (75, 215, 207), (192, 230, 91), (224, 148, 255), (255, 178, 73)];

    public static VariableWrap Create(IReadOnlyList<ProfileTagAssignment> tags, int maximum = int.MaxValue)
    {
        var row = new VariableWrap(6);
        Update(row, tags, maximum);
        return row;
    }

    public static void Update(VariableWrap row, IReadOnlyList<ProfileTagAssignment> tags, int maximum = int.MaxValue)
    {
        row.Children.Clear();
        var unique = tags.Where(tag => !string.IsNullOrWhiteSpace(tag.DisplayName))
            .DistinctBy(tag => tag.TagId, StringComparer.Ordinal).ToArray();
        foreach (var tag in unique.Take(maximum))
        {
            // The tag ID determines its saturated color across restarts, Themes and Packs.
            uint hash = 2166136261;
            foreach (var character in tag.TagId) hash = unchecked((hash ^ character) * 16777619);
            var color = Palette[hash % Palette.Length];
            var label = UI.Text(tag.DisplayName, "caption", maxLines: 1);
            label.Foreground = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 17, 23, 33));
            label.TextTrimming = TextTrimming.CharacterEllipsis;
            var chip = new Border
            {
                Child = label, MaxWidth = 150, Padding = new Thickness(8, 3, 8, 3),
                CornerRadius = new CornerRadius(3), BorderThickness = new Thickness(1),
                Background = new SolidColorBrush(Windows.UI.Color.FromArgb(255, color.R, color.G, color.B)),
                BorderBrush = new SolidColorBrush(Windows.UI.Color.FromArgb(255, (byte)(color.R * 0.65), (byte)(color.G * 0.65), (byte)(color.B * 0.65))),
            };
            ToolTipService.SetToolTip(chip, tag.DisplayName);
            AutomationProperties.SetName(chip, tag.DisplayName);
            row.Children.Add(chip);
        }
        if (unique.Length > maximum)
            row.Children.Add(UI.Badge("+" + (unique.Length - maximum)).Tip(
                string.Join(", ", unique.Skip(maximum).Select(tag => tag.DisplayName))));
        row.Visibility = unique.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
    }
}
