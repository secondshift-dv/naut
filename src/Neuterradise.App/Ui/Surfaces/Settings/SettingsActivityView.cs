using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Neuterradise.App.Activity;

namespace Neuterradise.App.Ui;

internal static class SettingsActivityView
{
    public static FrameworkElement Build(ActivityViewModel activity, Disposables bag)
    {
        var list = UI.V(0);
        var categories = UI.Wrap(6);
        var feedback = UI.WrappedText(string.Empty, "caption", "danger");

        void Fill()
        {
            categories.Children.Clear();
            foreach (var category in activity.Categories)
            {
                categories.Children.Add(UI.Chip(
                    category,
                    category == activity.SelectedCategory,
                    () => activity.FilterCategoryCommand.Execute(category)));
            }

            list.Children.Clear();
            list.Children.Add(UI.Surface(UI.Grid("auto", "128,*,136",
                UI.Text(UI.T("Activity.Table.Category", "Category"), "caption").At(0, 0),
                UI.Text(UI.T("Activity.Table.Event", "Activity"), "caption").At(0, 1),
                UI.Text(UI.T("Activity.Table.Date", "Date and time"), "caption").At(0, 2)), Material.Raised, 10, 8));
            foreach (var entry in activity.DisplayEntries.Take(300))
            {
                list.Children.Add(UI.Grid("auto", "128,*,136",
                    UI.WrappedText(entry.Category, "caption", "textSecondary").Margin(10, 10, 10, 10).At(0, 0),
                    UI.WrappedText(entry.Description, "body").Margin(10, 10, 10, 10).At(0, 1),
                    UI.WrappedText(entry.OccurredAtUtc.ToLocalTime().ToString("g"), "caption", "textSecondary").Margin(10, 10, 10, 10).At(0, 2)));
                list.Children.Add(UI.Divider());
            }

            feedback.Text = activity.HasError ? activity.ErrorMessage ?? string.Empty : string.Empty;
            feedback.Visibility = string.IsNullOrWhiteSpace(feedback.Text) ? Visibility.Collapsed : Visibility.Visible;
        }

        bag.Add(Observe.Collection(activity.DisplayEntries, Fill));
        bag.Add(Observe.Props(activity, Fill,
            nameof(ActivityViewModel.SelectedCategory),
            nameof(ActivityViewModel.Status),
            nameof(ActivityViewModel.ErrorMessage)));
        Fill();

        return UI.V(12,
            categories,
            feedback,
            list,
            UI.Button(UI.T("Activity.LoadMore", "Show more"), null, ButtonKind.Ghost, command: activity.LoadMoreCommand));
    }

}
