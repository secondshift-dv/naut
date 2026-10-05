using System.ComponentModel;
using System.Net;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Neuterradise.App.Import;
using Neuterradise.App.Import.Intake;
using Neuterradise.App.Presentation;
using Neuterradise.App.Shell;
using Windows.ApplicationModel.DataTransfer;

namespace Neuterradise.App.Ui;

/// <summary>
/// Import: a quiet, human workflow. Drop/Pick → Finding media → Choose Profile → Preparing → Review → Add to Vault → Done,
/// with truthful live activity controls: Pause/Prioritize/Cancel while running or Start/Resume/Cancel when paused.
/// Durable work keeps going when the person navigates away; there is no manual Refresh step.
/// </summary>
public sealed class ImportSurface : Surface
{
    private readonly ImportViewModel _vm;
    private readonly Grid _root = new();
    private readonly StackPanel _list = UI.V(10);
    private readonly Grid _wizardHost = new() { Visibility = Visibility.Collapsed };
    private readonly TextBlock _intake = UI.Text(string.Empty, "body-muted", maxLines: 2);
    private readonly VariableWrap _filters = new(6);
    private readonly Dictionary<Guid, ImportUnitRow> _rows = [];
    private FrameworkElement? _emptyActivity;
    private FrameworkElement? _visualPickerDialog;
    private Action? _closeVisualPicker;
    private ImportWizardViewModel? _wizard;
    private Disposables? _wizardBag;
    private Disposables? _wizardRenderBag;

    public ImportSurface(AppServices services, ImportViewModel vm) : base(services)
    {
        _vm = vm;
        _root.Background = ThemeRuntime.Current.Brush("canvas");

        var page = UI.V(12,
            UI.V(2, UI.Text(UI.T("Nav.Import", "Import"), "page-title"), UI.Text(_vm.MoveSummary, "body-muted", maxLines: 3)),
            DropZone(),
            _intake,
            UI.Wrap(8, UI.Text(UI.T("Import.Activity", "Your imports"), "section-title"), _filters),
            _list);
        page.MaxWidth = 1080;
        var scroll = UI.Scroll(page);
        _root.SizeChanged += (_, _) => page.Margin = new Thickness(UI.PagePadding(_root.ActualWidth), 14, UI.PagePadding(_root.ActualWidth), 28);
        _root.Children.Add(scroll);
        _root.Children.Add(_wizardHost);

        Bag.Add(Observe.Collection(_vm.FilteredUnits, ReconcileRows));
        Bag.Add(Observe.Props(_vm, UpdateHeader, nameof(ImportViewModel.IntakeStatusMessage), nameof(ImportViewModel.HasIntakeStatus), nameof(ImportViewModel.IsActiveFilter), nameof(ImportViewModel.IsAttentionFilter), nameof(ImportViewModel.IsHistoryFilter), nameof(ImportViewModel.HasAttention), nameof(ImportViewModel.HasClearableHistory)));
        Bag.Add(Observe.Props(_vm, UpdateWizard, nameof(ImportViewModel.Wizard)));
    }

    public override FrameworkElement View => _root;

    public override Neuterradise.App.Shell.ScreenStateViewModel Model => _vm;

    protected override void OnActivated(AppRoute route)
    {
        // ImportActivityService owns the live polling loop. Entering the page only wakes that loop;
        // it must not turn a transient database read into a page-level "could not refresh" notice.
        Services.ImportActivity.RequestRefresh();
    }

    protected override void OnSuspended()
    {
        CloseVisualPicker();
        HoverVideoCoordinator.Shared.StopAll();
    }

    /// <summary>Starts intake for paths picked elsewhere (e.g. "Add media" on a Profile).</summary>
    public Task IntakeAsync(
        IEnumerable<string> paths,
        Guid? destinationProfileId,
        IntakeOrigin origin = IntakeOrigin.Picker) =>
        _vm.IntakeSourcesAsync(paths, origin, destinationProfileId);

    private FrameworkElement DropZone()
    {
        var theme = ThemeRuntime.Current;
        var zone = new Border
        {
            AllowDrop = true,
            CornerRadius = new CornerRadius(theme.Tokens.Number("radiusSurface", 16)),
            BorderBrush = theme.Brush("borderDefault"),
            BorderThickness = new Thickness(1.5),
            Background = theme.Brush("surface1"),
            Padding = new Thickness(12),
            Child = UI.V(10,
                new IconView("icon.navigation.import", 24, "accent").Align(HorizontalAlignment.Center),
                UI.Text(UI.T("Import.Drop", "Drop photos, videos, 3D models or folders here"), "body-strong", maxLines: 2).Align(HorizontalAlignment.Center),
                new VariableWrap(
                    8,
                    UI.Button(UI.T("Import.ChooseFiles", "Choose files"), () => Services.RunUserAction(PickFilesAsync(), "ImportSurface.PickFilesAsync", UI.T("Import.PickerFailed", "The requested items could not be selected.")), ButtonKind.Primary),
                    UI.Button(UI.T("Import.ChooseFolder", "Choose folder"), () => Services.RunUserAction(PickFolderAsync(), "ImportSurface.PickFolderAsync", UI.T("Import.PickerFailed", "The requested items could not be selected.")))
                    .Align(HorizontalAlignment.Center))),
        };
        zone.DragOver += (_, e) =>
        {
            e.AcceptedOperation = DataPackageOperation.Copy;
            zone.BorderBrush = theme.Brush("accent");
        };
        zone.DragLeave += (_, _) => zone.BorderBrush = theme.Brush("borderDefault");
        zone.Drop += (_, e) => Services.RunUserAction(
            HandleDropAsync(zone, theme, e),
            "ImportSurface.DropAsync",
            UI.T("Import.DropFailed", "Those items could not be imported."));
        return zone;
    }

    private async Task HandleDropAsync(Border zone, ThemeRuntime theme, DragEventArgs args)
    {
        zone.BorderBrush = theme.Brush("borderDefault");
        if (!args.DataView.Contains(StandardDataFormats.StorageItems))
        {
            return;
        }

        var items = await args.DataView.GetStorageItemsAsync();
        var paths = items.Select(item => item.Path).Where(path => !string.IsNullOrWhiteSpace(path)).ToList();
        if (paths.Count > 0)
        {
            await _vm.IntakeSourcesAsync(paths, IntakeOrigin.DragDrop).ConfigureAwait(true);
        }
    }

    private async Task PickFilesAsync()
    {
        var files = await Services.PickFilesAsync(true).ConfigureAwait(true);
        if (files.Count > 0)
        {
            await _vm.IntakeSourcesAsync(files).ConfigureAwait(true);
        }
    }

    private async Task PickFolderAsync()
    {
        var folder = await Services.PickFolderAsync().ConfigureAwait(true);
        if (!string.IsNullOrWhiteSpace(folder))
        {
            await _vm.IntakeSourcesAsync([folder]).ConfigureAwait(true);
        }
    }

    private void UpdateHeader()
    {
        _intake.Text = _vm.IntakeStatusMessage ?? string.Empty;
        _intake.Visibility = _vm.HasIntakeStatus ? Visibility.Visible : Visibility.Collapsed;
        _filters.Children.Clear();
        _filters.Children.Add(UI.Chip(UI.T("Import.Filter.Active", "In progress"), _vm.IsActiveFilter, () => _vm.SelectFilterCommand.Execute(ImportActivityFilter.Active)));
        _filters.Children.Add(UI.Chip(UI.T("Import.Filter.Attention", "Needs attention") + (_vm.HasAttention ? " •" : string.Empty), _vm.IsAttentionFilter, () => _vm.SelectFilterCommand.Execute(ImportActivityFilter.NeedsAttention)));
        _filters.Children.Add(UI.Chip(UI.T("Import.Filter.History", "Finished"), _vm.IsHistoryFilter, () => _vm.SelectFilterCommand.Execute(ImportActivityFilter.History)));
        if (_vm.IsHistoryFilter && _vm.HasClearableHistory)
        {
            _filters.Children.Add(UI.Button(
                    UI.T("Import.ClearAll", "Clear All"),
                    null,
                    ButtonKind.Ghost,
                    command: _vm.ClearHistoryCommand)
                .Tip(UI.T("Import.ClearAll.Tooltip", "Remove all finished imports from history")));
        }
        ReconcileRows();
    }

    private void ReconcileRows()
    {
        if (_vm.ShowEmptyActivity)
        {
            RetireAllRows();
            if (_emptyActivity is not null)
            {
                _list.Children.Remove(_emptyActivity);
            }
            _emptyActivity = UI.Surface(UI.V(4, UI.Text(_vm.EmptyActivityTitle, "body-strong"), UI.Text(_vm.EmptyActivityMessage, "body-muted")), Material.Grounded, 12, 12);
            _list.Children.Add(_emptyActivity);
            return;
        }

        if (_emptyActivity is not null)
        {
            _list.Children.Remove(_emptyActivity);
            _emptyActivity = null;
        }

        var wanted = _vm.FilteredUnits.Select(unit => unit.UnitId).ToHashSet();
        foreach (var retired in _rows.Where(pair => !wanted.Contains(pair.Key)).ToList())
        {
            _list.Children.Remove(retired.Value.View);
            retired.Value.Dispose();
            _rows.Remove(retired.Key);
        }

        for (var index = 0; index < _vm.FilteredUnits.Count; index++)
        {
            var unit = _vm.FilteredUnits[index];
            if (!_rows.TryGetValue(unit.UnitId, out var row))
            {
                row = new ImportUnitRow(_vm, unit);
                _rows.Add(unit.UnitId, row);
            }

            var currentIndex = _list.Children.IndexOf(row.View);
            if (currentIndex == index)
            {
                continue;
            }
            if (currentIndex >= 0)
            {
                _list.Children.RemoveAt(currentIndex);
            }
            _list.Children.Insert(index, row.View);
        }
    }

    private void RetireAllRows()
    {
        foreach (var row in _rows.Values)
        {
            _list.Children.Remove(row.View);
            row.Dispose();
        }
        _rows.Clear();
    }

    private sealed class ImportUnitRow : IDisposable
    {
        private readonly ImportViewModel _owner;
        private readonly ImportUnitItemViewModel _unit;
        private readonly TextBlock _title;
        private readonly TextBlock _stage;
        private readonly Border _priority;
        private readonly TextBlock _detail;
        private readonly ImportProgressView _progress;
        private readonly TextBlock _progressText;
        private readonly VariableWrap _actions;
        private readonly IDisposable _subscription;

        public ImportUnitRow(ImportViewModel owner, ImportUnitItemViewModel unit)
        {
            _owner = owner;
            _unit = unit;
            _title = UI.Text(string.Empty, "body-strong", maxLines: 1);
            _stage = UI.Text(string.Empty, "metadata");
            _priority = UI.Badge(UI.T("Import.Priority", "Priority"), "accent");
            _detail = UI.Text(string.Empty, "caption", maxLines: 4);
            _progress = new ImportProgressView { Margin = new Thickness(0, 6, 0, 0) };
            _progressText = UI.Text(string.Empty, "caption");
            _actions = new VariableWrap(6);
            View = UI.Surface(UI.V(4,
                UI.Grid("auto", "*,auto", _title.At(0, 0), UI.H(6, _priority, _stage).At(0, 1)),
                _detail,
                _progress,
                _progressText,
                _actions.Margin(0, 6, 0, 0)), Material.Grounded, 12, 12);
            _subscription = Observe.Props(unit, Apply);
        }

        public FrameworkElement View { get; }

        private void Apply()
        {
            _title.Text = WebUtility.HtmlDecode(_unit.SourceDisplayName);
            _stage.Text = _unit.StageText;
            _stage.Foreground = ThemeRuntime.Current.Brush(_unit.NeedsAttention ? "textWarning" : "textSecondary");
            _priority.Visibility = _unit.IsPriority ? Visibility.Visible : Visibility.Collapsed;
            _detail.Text = _unit.DetailText;
            _progress.Update(
                _unit.ShowProgress,
                _unit.IsIndeterminate,
                _unit.ProgressValue,
                _unit.IsActive);
            _progressText.Text = _unit.ProgressText ?? string.Empty;
            _actions.Children.Clear();
            if (_unit.CanContinue)
            {
                var actionText = _unit.Stage == ImportActivityStage.ReadyToVerify
                    ? UI.T("Import.Verify", "Review")
                    : UI.T("Import.ChooseProfile", "Choose Profile");
                _actions.Children.Add(UI.Button(actionText, null, ButtonKind.Primary, command: _owner.ContinueImportCommand, parameter: _unit));
            }
            if (_unit.ShowTransportControls)
            {
                if (_unit.CanStart)
                {
                    _actions.Children.Add(UI.Button(
                        UI.T("Import.Start", "Start"),
                        null,
                        ButtonKind.Primary,
                        "icon.action.play",
                        _owner.StartUnitCommand,
                        _unit).Tip(_unit.TransportTooltip));
                }
                else
                {
                    var pause = UI.Button(UI.T("Import.Pause", "Pause"), null, icon: "icon.action.pause", command: _owner.PauseUnitCommand, parameter: _unit)
                        .Tip(UI.T("Import.Pause.Tooltip", "Pause import"));
                    pause.IsEnabled = _unit.CanPause;
                    _actions.Children.Add(pause);

                    if (_unit.CanPrioritize)
                    {
                        _actions.Children.Add(UI.Button(
                            UI.T("Import.Prioritize", "Prioritize"),
                            null,
                            ButtonKind.Primary,
                            "icon.action.play",
                            _owner.StartUnitCommand,
                            _unit).Tip(_unit.TransportTooltip));
                    }
                }
            }
            if (_unit.CanRetry) _actions.Children.Add(UI.Button(UI.T("Import.Retry", "Retry"), null, command: _owner.RetryUnitCommand, parameter: _unit));
            if (_unit.CanOpenProfile) _actions.Children.Add(UI.Button(UI.T("Import.OpenProfile", "Open Profile"), null, ButtonKind.Ghost, command: _owner.OpenProfileCommand, parameter: _unit));
            if (_unit.CanClearHistory) _actions.Children.Add(UI.Button(UI.T("Import.Clear", "Clear"), null, ButtonKind.Ghost, command: _owner.ClearHistoryItemCommand, parameter: _unit)
                .Tip(UI.T("Import.Clear.Tooltip", "Remove this import from history")));
            if (_unit.CanCancel)
            {
                _actions.Children.Add(UI.Button(
                    UI.T("Import.Cancel", "Cancel"),
                    () =>
                    {
                        _owner.IntakeStatusMessage = UI.T(
                            "Import.CancelRequested",
                            "Cancellation requested. Running work will stop at the next safe interruption point.");
                        _owner.CancelUnitCommand.Execute(_unit);
                    },
                    ButtonKind.Ghost));
            }
        }

        public void Dispose()
        {
            _subscription.Dispose();
            _progress.Retire();
        }
    }

    /// <summary>
    /// Determinate progress stays numerically truthful while a subtle diagonal sweep communicates
    /// that the worker is still alive between percentage changes. Reduced Motion removes the sweep,
    /// and indeterminate work delegates animation to the native ProgressBar.
    /// </summary>
    private sealed class ImportProgressView : Grid
    {
        private const double BarHeight = 5;
        private const double SweepWidth = 18;

        private readonly ProgressBar _bar;
        private readonly Grid _activityClip;
        private readonly Border _sweep;
        private readonly CompositeTransform _sweepTransform = new() { SkewX = -24 };
        private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromMilliseconds(75) };

        private double _value;
        private double _phase;
        private bool _shouldAnimate;
        private bool _disposed;

        public ImportProgressView()
        {
            Height = BarHeight;
            VerticalAlignment = VerticalAlignment.Center;

            _bar = new ProgressBar
            {
                Minimum = 0,
                Maximum = 1,
                Height = BarHeight,
                HorizontalAlignment = HorizontalAlignment.Stretch,
                VerticalAlignment = VerticalAlignment.Center,
            };

            _sweep = new Border
            {
                Width = SweepWidth,
                Height = BarHeight,
                HorizontalAlignment = HorizontalAlignment.Left,
                VerticalAlignment = VerticalAlignment.Center,
                Background = ThemeRuntime.Current.Brush("surface1"),
                Opacity = 0.42,
                RenderTransform = _sweepTransform,
                IsHitTestVisible = false,
            };

            _activityClip = new Grid
            {
                Height = BarHeight,
                HorizontalAlignment = HorizontalAlignment.Left,
                VerticalAlignment = VerticalAlignment.Center,
                IsHitTestVisible = false,
            };
            _activityClip.Children.Add(_sweep);

            Children.Add(_bar);
            Children.Add(_activityClip);

            SizeChanged += OnSizeChanged;
            _timer.Tick += OnTick;
            ReducedMotionAuthority.Changed += OnReducedMotionChanged;
            UpdateAnimation();
        }

        public void Update(bool visible, bool indeterminate, double value, bool active)
        {
            Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
            _value = Math.Clamp(value, 0, 1);
            _bar.Value = _value;
            _bar.IsIndeterminate = indeterminate;
            _shouldAnimate = visible && active && !indeterminate && _value > 0 && _value < 1;
            UpdateClip();
            UpdateAnimation();
        }

        private void OnSizeChanged(object sender, SizeChangedEventArgs args) => UpdateClip();

        private void UpdateClip()
        {
            var width = Math.Max(0, ActualWidth * _value);
            _activityClip.Width = width;
            _activityClip.Clip = new RectangleGeometry
            {
                Rect = new Windows.Foundation.Rect(0, 0, width, BarHeight),
            };
            _activityClip.Visibility = width > 1 ? Visibility.Visible : Visibility.Collapsed;
            if (_phase > width + SweepWidth)
            {
                _phase = 0;
            }
        }

        private void OnTick(object? sender, object e)
        {
            var width = _activityClip.Width;
            if (!_shouldAnimate || ReducedMotionAuthority.IsReduced || width <= 1)
            {
                return;
            }

            var travel = width + (SweepWidth * 2);
            var step = Math.Clamp(width / 22, 2.5, 8);
            _phase = (_phase + step) % travel;
            _sweepTransform.TranslateX = _phase - SweepWidth;
        }

        private void OnReducedMotionChanged(object? sender, EventArgs args) => UpdateAnimation();

        private void UpdateAnimation()
        {
            var animate = _shouldAnimate && !ReducedMotionAuthority.IsReduced && Visibility == Visibility.Visible;
            _sweep.Visibility = animate ? Visibility.Visible : Visibility.Collapsed;
            if (animate)
            {
                if (!_timer.IsEnabled)
                {
                    _timer.Start();
                }
            }
            else
            {
                _timer.Stop();
                _phase = 0;
                _sweepTransform.TranslateX = -SweepWidth;
            }
        }

        public void Retire()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _timer.Stop();
            _timer.Tick -= OnTick;
            SizeChanged -= OnSizeChanged;
            ReducedMotionAuthority.Changed -= OnReducedMotionChanged;
        }
    }

    // ---------------------------------------------------------------- wizard

    private void UpdateWizard()
    {
        if (ReferenceEquals(_wizard, _vm.Wizard))
        {
            return;
        }

        _wizardBag?.Dispose();
        _wizardRenderBag?.Dispose();
        _wizardBag = new Disposables();
        _wizard = _vm.Wizard;
        _wizardHost.Children.Clear();
        if (_wizard is null)
        {
            _wizardHost.Visibility = Visibility.Collapsed;
            return;
        }

        var theme = ThemeRuntime.Current;
        var wizard = _wizard;
        var content = new Grid();
        var body = UI.V(12);
        var scroll = UI.Scroll(body);
        scroll.VerticalAlignment = VerticalAlignment.Top;
        scroll.ViewChanged += (_, _) => HoverVideoCoordinator.Shared.StopAll();
        var footer = UI.Wrap(6);
        var error = UI.WrappedText(string.Empty, "caption", "danger");
        var stepLabel = UI.Text(string.Empty, "page-title");
        var destinationNumber = UI.Text("1", "numeric").Align(HorizontalAlignment.Center, VerticalAlignment.Center);
        var reviewNumber = UI.Text("2", "numeric").Align(HorizontalAlignment.Center, VerticalAlignment.Center);
        Border StepMarker(TextBlock number) => new()
        {
            Child = number, Width = 26, Height = 26, CornerRadius = new CornerRadius(13),
            VerticalAlignment = VerticalAlignment.Center,
        };
        var destinationMarker = StepMarker(destinationNumber);
        var reviewMarker = StepMarker(reviewNumber);
        Border StepNode(Border marker, string label)
        {
            var node = UI.Grid("auto", "auto,*", marker.At(0, 0),
                UI.WrappedText(label, "control").Align(vertical: VerticalAlignment.Center).At(0, 1));
            node.ColumnSpacing = 8;
            return UI.Surface(node, Material.Grounded, 10, 8);
        }
        var destinationStep = StepNode(destinationMarker, UI.T("Import.Step.Who", "Who is this for?"));
        var reviewStep = StepNode(reviewMarker, UI.T("Import.Passport.Verify", "REVIEW"));
        var stepArrow = new IconView("icon.action.back", 22, "textAccent")
        {
            RenderTransformOrigin = new Windows.Foundation.Point(0.5, 0.5),
            RenderTransform = new ScaleTransform { ScaleX = -1 },
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        };
        var steps = UI.Grid("auto", "*,32,*", destinationStep.At(0, 0), stepArrow.At(0, 1), reviewStep.At(0, 2));
        var header = UI.V(8,
            UI.V(3, stepLabel, UI.Text(WebUtility.HtmlDecode(wizard.SourceDisplayName), "caption", "textSecondary", 1)),
            steps);
        var footerFrame = new Border
        {
            Child = footer.Align(HorizontalAlignment.Right),
            Padding = new Thickness(0, 12, 0, 0),
            BorderBrush = theme.Brush("borderSubtle"),
            BorderThickness = new Thickness(0, 1, 0, 0),
        };
        var card = UI.Surface(UI.Grid("auto,auto,auto,auto", "*",
            header.At(0),
            scroll.Margin(0, 12, 0, 12).At(1),
            error.Margin(0, 0, 0, 8).At(2),
            footerFrame.At(3)), Material.Grounded, theme.Tokens.Number("radiusSurface", 12), 16);
        card.BorderBrush = theme.Brush("borderDefault");
        card.MaxWidth = 800;
        card.MaxHeight = 720;
        card.Margin = new Thickness(12);
        void UpdateWizardEnvelope()
        {
            card.Width = Math.Max(0, Math.Min(800, content.ActualWidth - 24));
            card.MaxHeight = Math.Max(0, Math.Min(640, content.ActualHeight - 24));
            scroll.MaxHeight = Math.Max(0, card.MaxHeight - header.ActualHeight - footerFrame.ActualHeight - error.ActualHeight - 66);
        }
        content.SizeChanged += (_, _) => UpdateWizardEnvelope();
        header.SizeChanged += (_, _) => UpdateWizardEnvelope();
        footerFrame.SizeChanged += (_, _) => UpdateWizardEnvelope();
        error.SizeChanged += (_, _) => UpdateWizardEnvelope();
        card.HorizontalAlignment = HorizontalAlignment.Center;
        card.VerticalAlignment = VerticalAlignment.Center;
        content.Background = theme.Brush("scrimMedium");
        content.Children.Add(card);
        _wizardHost.Children.Add(content);
        _wizardHost.Visibility = Visibility.Visible;

        void Render()
        {
            _wizardRenderBag?.Dispose();
            _wizardRenderBag = new Disposables();
            body.Children.Clear();
            footer.Children.Clear();
            var preparation = UI.Text(string.Empty, "caption");
            var mediaSummary = UI.Text(string.Empty, "body-muted", maxLines: 2);
            body.Children.Add(preparation);
            body.Children.Add(mediaSummary);
            _wizardRenderBag.Add(Observe.Props(wizard, () =>
            {
                preparation.Text = wizard.PreparationText;
                preparation.Visibility = string.IsNullOrWhiteSpace(preparation.Text) ? Visibility.Collapsed : Visibility.Visible;
                mediaSummary.Text = wizard.MediaSummaryText;
            }, nameof(ImportWizardViewModel.PreparationText), nameof(ImportWizardViewModel.MediaSummaryText)));
            if (wizard.IsChooseProfileMode)
            {
                BuildProfileStep(wizard, body, _wizardRenderBag);
                footer.Children.Add(UI.Button(UI.T("Import.Close", "Close"), null, ButtonKind.Ghost, command: wizard.CloseCommand));
                footer.Children.Add(UI.Button(UI.T("Import.ConfirmDestination", "Confirm destination"), null, ButtonKind.Primary, command: wizard.ContinueCommand));
            }
            else
            {
                BuildDetailsStep(wizard, body, _wizardRenderBag);
                footer.Children.Add(UI.Button(UI.T("Import.Close", "Close"), null, ButtonKind.Ghost, command: wizard.CloseCommand));
                footer.Children.Add(UI.Button(UI.T("Import.Save", "Add to Vault"), null, ButtonKind.Primary, "icon.navigation.import", command: wizard.ImportCommand));
            }
        }

        _wizardBag.Add(Observe.Props(wizard, () =>
        {
            stepLabel.Text = wizard.StepTitle;
            destinationStep.Background = theme.Brush(wizard.IsChooseProfileMode ? "surfaceSelected" : "surface2");
            destinationStep.BorderBrush = theme.Brush(wizard.IsChooseProfileMode ? "borderInteractive" : "borderSubtle");
            reviewStep.Background = theme.Brush(wizard.IsChooseProfileMode ? "surface2" : "surfaceSelected");
            reviewStep.BorderBrush = theme.Brush(wizard.IsChooseProfileMode ? "borderSubtle" : "borderInteractive");
            destinationMarker.Background = theme.Brush(wizard.IsChooseProfileMode ? "accent" : "surface3");
            destinationNumber.Foreground = theme.Brush(wizard.IsChooseProfileMode ? "textOnAccent" : "textSecondary");
            destinationNumber.Text = wizard.IsChooseProfileMode ? "1" : "✓";
            reviewMarker.Background = theme.Brush(wizard.IsChooseProfileMode ? "surface3" : "accent");
            reviewNumber.Foreground = theme.Brush(wizard.IsChooseProfileMode ? "textSecondary" : "textOnAccent");
            error.Text = wizard.ErrorMessage ?? string.Empty;
            error.Visibility = wizard.HasError ? Visibility.Visible : Visibility.Collapsed;
        }, nameof(ImportWizardViewModel.StepIndicator), nameof(ImportWizardViewModel.StepTitle), nameof(ImportWizardViewModel.ErrorMessage), nameof(ImportWizardViewModel.HasError)));
        _wizardBag.Add(Observe.Props(wizard, Render, nameof(ImportWizardViewModel.Choice)));
    }

    private static void BuildProfileStep(ImportWizardViewModel wizard, StackPanel body, Disposables lifetime)
    {
        var theme = ThemeRuntime.Current;

        void CommitOnEnter(TextBox textBox)
        {
            textBox.KeyDown += (_, e) =>
            {
                if (e.Key != Windows.System.VirtualKey.Enter
                    || !wizard.ContinueCommand.CanExecute(null))
                {
                    return;
                }

                e.Handled = true;
                wizard.ContinueCommand.Execute(null);
            };
        }

        Button SelectionCard(
            string title,
            string description,
            string icon,
            bool selected,
            Action onClick,
            string? badge = null)
        {
            FrameworkElement state = selected
                ? UI.Badge(UI.T("Import.Selected", "Selected"), "accent")
                : badge is null ? new Grid() : UI.Badge(badge);
            var titleRow = UI.Grid("auto", "*,auto",
                UI.Text(title, "body-strong", maxLines: 1).At(0, 0),
                state.At(0, 1));
            titleRow.ColumnSpacing = 8;
            var content = UI.Grid(
                "auto",
                "auto,*",
                new IconView(icon, 22, selected ? "accent" : "textSecondary")
                    .Margin(0, 2, 12, 0)
                    .At(0, 0),
                UI.V(
                    3,
                    titleRow,
                    UI.Text(description, "caption", "textSecondary", 3))
                    .At(0, 1));

            var button = new Button
            {
                Content = content,
                MinHeight = 52,
                Padding = new Thickness(10, 8, 10, 8),
                CornerRadius = new CornerRadius(theme.Tokens.Number("radiusCard", 12)),
                Background = theme.Brush(selected ? "surfaceSelected" : "surface1"),
                BorderBrush = theme.Brush(selected ? "accent" : "borderSubtle"),
                BorderThickness = new Thickness(1),
                HorizontalContentAlignment = HorizontalAlignment.Stretch,
                VerticalContentAlignment = VerticalAlignment.Center,
            };
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(button, title);
            button.Click += (_, _) => onClick();
            return button;
        }

        Button ProfileResultCard(
            ProfileLookupItemViewModel profile,
            bool selected,
            Action onClick)
        {
            const double cardWidth = 244;
            const double coverHeight = 52;
            var coverHost = new Grid
            {
                Width = coverHeight,
                Height = coverHeight,
                Background = theme.Brush("surface2"),
            };

            if (profile.CoverSource is { } cover)
            {
                coverHost.Children.Add(new SkImageView
                {
                    Source = cover,
                    Width = coverHeight,
                    Height = coverHeight,
                    CornerRadiusValue = 9,
                });
            }
            else
            {
                coverHost.Children.Add(new Border
                {
                    Background = theme.Brush("surface2"),
                    CornerRadius = new CornerRadius(9),
                    Child = new IconView("icon.profile.person", 28, "textMuted")
                        .Align(HorizontalAlignment.Center, VerticalAlignment.Center),
                });
            }

            var categoryName = string.IsNullOrWhiteSpace(profile.CategoryName)
                ? UI.T("Import.Profile.Uncategorized", "Uncategorized")
                : profile.CategoryName;
            var text = UI.V(
                1,
                UI.Text(profile.DisplayName, "body-strong", maxLines: 1),
                UI.Text(categoryName, "caption", "textSecondary", 1));
            var card = new Button
            {
                Content = UI.Grid("auto", "auto,*", coverHost.Margin(0, 0, 10, 0).At(0, 0), text.Align(vertical: VerticalAlignment.Center).At(0, 1)),
                Width = cardWidth,
                MinHeight = 72,
                Padding = new Thickness(6),
                CornerRadius = new CornerRadius(theme.Tokens.Number("radiusCard", 12)),
                Background = theme.Brush(selected ? "surfaceSelected" : "surface1"),
                BorderBrush = theme.Brush(selected ? "accent" : "borderSubtle"),
                BorderThickness = new Thickness(1),
                HorizontalContentAlignment = HorizontalAlignment.Stretch,
                VerticalContentAlignment = VerticalAlignment.Top,
            };
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(
                card,
                UI.F(
                    "Import.ProfilePicker.CardAutomation",
                    "{0}, {1}{2}",
                    profile.DisplayName,
                    categoryName,
                    selected ? ", selected" : string.Empty));
            card.Click += (_, _) => onClick();
            return card;
        }

        body.Children.Add(DetailLayout.Heading(
            UI.T("Import.ProfilePicker.GuidanceTitle", "Choose where these media belong"),
            UI.T(
                "Import.ProfilePicker.GuidanceText",
                "Use an existing Profile when the person is already in your Vault. Create a new Profile only for a new person."), "icon.profile.person"));

        var choiceGrid = UI.FormColumns(
            SelectionCard(
                    UI.T("Import.Choice.Existing", "Existing Profile"),
                    UI.T("Import.Choice.Existing.Description", "Find someone who is already in your Vault."),
                    "icon.profile.person",
                    wizard.IsExistingChoice,
                    () => wizard.ChooseExistingCommand.Execute(null))
                .At(0, 0),
            SelectionCard(
                    UI.T("Import.Choice.New", "New Profile"),
                    UI.T("Import.Choice.New.Description", "Create a Profile for a person who is not in your Vault yet."),
                    "icon.action.add",
                    wizard.IsNewChoice,
                    () => wizard.ChooseNewCommand.Execute(null))
                .At(0, 1));

        body.Children.Add(UI.Section(
            UI.T("Import.Step.Who", "Who is this for?"),
            UI.T("Import.Step.Who.Description", "Pick one destination. You can review Profile details before adding the import to Vault."),
            choiceGrid));

        if (wizard.IsNewChoice)
        {
            var name = new TextBox
            {
                Header = UI.T("Import.NewName.Label", "Profile name"),
                PlaceholderText = UI.T("Import.NewName", "Profile name"),
                Text = wizard.NewProfileName,
                HorizontalAlignment = HorizontalAlignment.Stretch,
            };
            CommitOnEnter(name);
            var synchronizingName = false;
            name.TextChanged += (_, _) =>
            {
                if (!synchronizingName)
                {
                    wizard.NewProfileName = name.Text;
                }
            };
            lifetime.Add(Observe.Props(wizard, () =>
            {
                if (string.Equals(name.Text, wizard.NewProfileName, StringComparison.Ordinal))
                {
                    return;
                }

                synchronizingName = true;
                name.Text = wizard.NewProfileName;
                synchronizingName = false;
            }, nameof(ImportWizardViewModel.NewProfileName)));

            var validation = UI.V(6);
            void UpdateValidation()
            {
                validation.Children.Clear();
                if (wizard.NameValidationText is { } message)
                {
                    validation.Children.Add(UI.WrappedText(message, "caption", "warning"));
                }

                if (wizard.HasDuplicateName)
                {
                    validation.Children.Add(UI.Guidance(
                        UI.T("Import.DuplicateProfile.Title", "A Profile with this name already exists"),
                        wizard.DuplicateNameText,
                        "warning"));
                    validation.Children.Add(UI.Button(
                        UI.T("Import.UseExisting", "Use the existing Profile"),
                        null,
                        ButtonKind.Secondary,
                        command: wizard.UseDuplicateNameProfileCommand));
                }
            }
            lifetime.Add(Observe.Props(
                wizard,
                UpdateValidation,
                nameof(ImportWizardViewModel.NameValidationText),
                nameof(ImportWizardViewModel.HasDuplicateName),
                nameof(ImportWizardViewModel.DuplicateNameText)));

            body.Children.Add(DetailLayout.Section(
                UI.T("Import.NewProfile.Title", "Create Profile"),
                UI.T("Import.NewProfile.Description", "Start with the person's name. Category, tags and appearance can be reviewed before the import is added to Vault."),
                "icon.profile.person",
                name,
                validation));
        }
        else
        {
            var search = new TextBox
            {
                Header = UI.T("Import.SearchProfiles.Label", "Find a Profile"),
                PlaceholderText = UI.T("Import.SearchProfiles", "Search Profiles"),
                Text = wizard.ExistingSearchText,
                HorizontalAlignment = HorizontalAlignment.Stretch,
            };
            CommitOnEnter(search);
            var synchronizingSearch = false;
            search.TextChanged += (_, _) =>
            {
                if (!synchronizingSearch)
                {
                    wizard.ExistingSearchText = search.Text;
                }
            };
            lifetime.Add(Observe.Props(wizard, () =>
            {
                if (string.Equals(search.Text, wizard.ExistingSearchText, StringComparison.Ordinal))
                {
                    return;
                }

                synchronizingSearch = true;
                search.Text = wizard.ExistingSearchText;
                synchronizingSearch = false;
            }, nameof(ImportWizardViewModel.ExistingSearchText)));

            var list = new VariableWrap(10);
            void FillProfiles()
            {
                list.Children.Clear();
                foreach (var profile in wizard.ExistingProfiles.Take(24))
                {
                    var selected = wizard.SelectedExistingProfile?.ProfileId == profile.ProfileId;
                    list.Children.Add(ProfileResultCard(
                        profile,
                        selected,
                        () => wizard.SelectedExistingProfile = profile));
                }

                if (list.Children.Count == 0)
                {
                    list.Children.Add(UI.Text(
                        string.IsNullOrWhiteSpace(wizard.ExistingSearchText)
                            ? UI.T("Import.ProfilePicker.SearchPrompt", "Profiles will appear here. Start typing to narrow the list.")
                            : UI.T("Import.ProfilePicker.NoResults", "No Profile matches that search."),
                        "caption",
                        "textMuted",
                        3));
                }
            }

            lifetime.Add(Observe.Collection(wizard.ExistingProfiles, FillProfiles));
            lifetime.Add(Observe.Props(
                wizard,
                FillProfiles,
                nameof(ImportWizardViewModel.SelectedExistingProfile),
                nameof(ImportWizardViewModel.ExistingSearchText)));

            var resultsScroll = UI.Scroll(list);
            resultsScroll.MaxHeight = 250;
            body.Children.Add(UI.Surface(
                UI.V(10, search, resultsScroll),
                Material.Raised,
                12,
                14));
        }
    }

    private void BuildDetailsStep(ImportWizardViewModel wizard, StackPanel body, Disposables lifetime)
    {
        var theme = ThemeRuntime.Current;
        var profileSummary = UI.Text(string.Empty, "section-title", maxLines: 2);
        lifetime.Add(Observe.Props(
            wizard,
            () => profileSummary.Text = wizard.ProfileSummaryText,
            nameof(ImportWizardViewModel.ProfileSummaryText)));

        var portrait = new SkImageView
        {
            Width = 80,
            Height = 100,
            CornerRadiusValue = 10,
            Visibility = Visibility.Collapsed,
        };
        var portraitFallback = new Border
        {
            Width = 80,
            Height = 100,
            Background = theme.Brush("surface2"),
            CornerRadius = new CornerRadius(10),
            Child = UI.V(
                    5,
                    new IconView("icon.profile.person", 34, "textMuted"),
                    UI.Text(UI.T("Import.Passport.CoverPending", "Choose Cover"), "micro", "textMuted"))
                .Align(HorizontalAlignment.Center, VerticalAlignment.Center),
        };
        var portraitHost = new Grid { Width = 80, Height = 100 };
        portraitHost.Children.Add(portraitFallback);
        portraitHost.Children.Add(portrait);

        var bannerPreview = new SkImageView
        {
            Height = 88,
            CornerRadiusValue = 10,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            Visibility = Visibility.Collapsed,
        };
        var bannerFallback = new Border
        {
            Height = 88,
            Background = theme.Brush("surface2"),
            CornerRadius = new CornerRadius(10),
            Child = UI.V(
                    5,
                    new IconView("icon.media.video", 28, "textMuted"),
                    UI.Text(UI.T("Import.Passport.BannerPending", "Choose Banner"), "micro", "textMuted"))
                .Align(HorizontalAlignment.Center, VerticalAlignment.Center),
        };
        var bannerHost = new Grid { Height = 88 };
        bannerHost.Children.Add(bannerFallback);
        bannerHost.Children.Add(bannerPreview);

        ImportVisualOption? observedCover = null;
        PropertyChangedEventHandler? observedCoverHandler = null;
        void ApplyPortrait()
        {
            var source = wizard.SelectedCover?.Preview;
            portrait.Source = source;
            portrait.Visibility = source is null ? Visibility.Collapsed : Visibility.Visible;
            portraitFallback.Visibility = source is null ? Visibility.Visible : Visibility.Collapsed;
        }
        void BindPortrait()
        {
            if (observedCover is not null && observedCoverHandler is not null)
            {
                observedCover.PropertyChanged -= observedCoverHandler;
            }

            observedCover = wizard.SelectedCover;
            if (observedCover is not null)
            {
                observedCoverHandler = (_, args) =>
                {
                    if (args.PropertyName is nameof(ImportVisualOption.Preview) or nameof(ImportVisualOption.HasPreview))
                    {
                        UiDispatch.Run(ApplyPortrait);
                    }
                };
                observedCover.PropertyChanged += observedCoverHandler;
            }
            else
            {
                observedCoverHandler = null;
            }
            ApplyPortrait();
        }

        ImportVisualOption? observedBanner = null;
        PropertyChangedEventHandler? observedBannerHandler = null;
        void ApplyBanner()
        {
            var source = wizard.SelectedBanner?.Preview;
            bannerPreview.Source = source;
            bannerPreview.Visibility = source is null ? Visibility.Collapsed : Visibility.Visible;
            bannerFallback.Visibility = source is null ? Visibility.Visible : Visibility.Collapsed;
        }
        void BindBanner()
        {
            if (observedBanner is not null && observedBannerHandler is not null)
            {
                observedBanner.PropertyChanged -= observedBannerHandler;
            }

            observedBanner = wizard.SelectedBanner;
            if (observedBanner is not null)
            {
                observedBannerHandler = (_, args) =>
                {
                    if (args.PropertyName is nameof(ImportVisualOption.Preview) or nameof(ImportVisualOption.HasPreview))
                    {
                        UiDispatch.Run(ApplyBanner);
                    }
                };
                observedBanner.PropertyChanged += observedBannerHandler;
            }
            else
            {
                observedBannerHandler = null;
            }
            ApplyBanner();
        }

        lifetime.Add(Observe.Props(wizard, BindPortrait, nameof(ImportWizardViewModel.SelectedCover)));
        lifetime.Add(Observe.Props(wizard, BindBanner, nameof(ImportWizardViewModel.SelectedBanner)));
        lifetime.Add(() =>
        {
            if (observedCover is not null && observedCoverHandler is not null)
            {
                observedCover.PropertyChanged -= observedCoverHandler;
            }
            if (observedBanner is not null && observedBannerHandler is not null)
            {
                observedBanner.PropertyChanged -= observedBannerHandler;
            }
        });
        BindPortrait();
        BindBanner();

        var coverButton = new Button
        {
            Content = UI.V(
                5,
                portraitHost,
                UI.H(
                    4,
                    new IconView("icon.action.edit", 12, "textMuted"),
                    UI.Text(UI.T("Import.Passport.CoverAction", "Choose Cover"), "micro", "textMuted"))
                    .Align(HorizontalAlignment.Center)),
            Padding = new Thickness(5),
            Background = theme.Brush("surface1"),
            BorderBrush = theme.Brush("borderSubtle"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(11),
            HorizontalContentAlignment = HorizontalAlignment.Center,
            VerticalContentAlignment = VerticalAlignment.Top,
        };
        var showCoverPicker = VisualPickerDialog(wizard, isCover: true, lifetime);
        coverButton.Click += (_, _) => showCoverPicker();

        var bannerButton = new Button
        {
            Content = UI.V(
                5,
                bannerHost,
                UI.H(
                    4,
                    new IconView("icon.action.edit", 12, "textMuted"),
                    UI.Text(UI.T("Import.Passport.BannerAction", "Choose Banner"), "micro", "textMuted"))),
            Padding = new Thickness(6),
            Background = theme.Brush("surface1"),
            BorderBrush = theme.Brush("borderSubtle"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(11),
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
        };
        var showBannerPicker = VisualPickerDialog(wizard, isCover: false, lifetime);
        bannerButton.Click += (_, _) => showBannerPicker();

        var identityFields = UI.V(
            7,
            UI.Text(UI.T("Import.Passport.NameLabel", "Profile name"), "micro", "textMuted"),
            profileSummary);
        var documentHeader = UI.Grid(
            "auto",
            "*,auto",
            UI.V(
                    0,
                    UI.Text(UI.T("Import.Passport.Kicker", "Profile"), "micro", "textMuted"),
                    UI.Text(UI.T("Import.Passport.Title", "Identity"), "body-strong"))
                .At(0, 0),
            UI.Badge(UI.T("Import.Passport.Verify", "REVIEW"), "accent")
                .Align(HorizontalAlignment.Right, VerticalAlignment.Center)
                .At(0, 1));
        var identityGrid = UI.Grid(
            "auto",
            "auto,*",
            coverButton
                .Margin(0, 0, 12, 0)
                .At(0, 0),
            identityFields.At(0, 1));
        var document = UI.V(
            14,
            documentHeader,
            UI.V(
                5,
                UI.Text(UI.T("Import.Passport.BannerLabel", "Banner"), "micro", "textMuted"),
                bannerButton),
            UI.Guidance(
                UI.T("Import.Passport.AppearanceLaterTitle", "You can change these later"),
                UI.T(
                    "Import.Passport.AppearanceLater",
                    "Cover and Banner can be changed later from Profile customization.")),
            UI.Divider(),
            identityGrid);

        if (wizard.ShowNewProfileDetails)
        {
            var category = new ComboBox
            {
                MinWidth = 220,
                HorizontalAlignment = HorizontalAlignment.Stretch,
            };
            var synchronizingCategory = false;
            void UpdateCategory()
            {
                synchronizingCategory = true;
                try
                {
                    category.ItemsSource = new[] { UI.T("Import.NoCategory", "No category") }
                        .Concat(wizard.Categories.Select(c => c.Name))
                        .ToList();
                    var index = wizard.Categories.ToList()
                        .FindIndex(c => c.CategoryId == wizard.SelectedCategoryId);
                    category.SelectedIndex = index + 1;
                }
                finally
                {
                    synchronizingCategory = false;
                }
            }
            category.SelectionChanged += (_, _) =>
            {
                if (!synchronizingCategory)
                {
                    wizard.SelectedCategoryId = category.SelectedIndex <= 0
                        ? null
                        : wizard.Categories[category.SelectedIndex - 1].CategoryId;
                }
            };
            lifetime.Add(Observe.Collection(wizard.Categories, UpdateCategory));
            lifetime.Add(Observe.Props(
                wizard,
                UpdateCategory,
                nameof(ImportWizardViewModel.SelectedCategoryId)));

            var newCategory = new TextBox
            {
                PlaceholderText = UI.T("Import.NewCategory.Placeholder", "Category name"),
                Text = wizard.NewCategoryName,
                HorizontalAlignment = HorizontalAlignment.Stretch,
            };
            var synchronizingNewCategory = false;
            newCategory.TextChanged += (_, _) =>
            {
                if (!synchronizingNewCategory)
                {
                    wizard.NewCategoryName = newCategory.Text;
                }
            };

            var addCategory = UI.Button(
                UI.T("Import.CreateCategory", "New category"),
                null,
                ButtonKind.Ghost,
                "icon.action.add");
            string? categoryBeforeCreate = null;
            var categoryEditor = UI.Grid("auto", "*,auto,auto");
            categoryEditor.Visibility = Visibility.Collapsed;
            var createCategory = UI.Button(
                UI.T("Import.CreateCategory.Action", "Create"),
                null,
                ButtonKind.Secondary,
                command: wizard.CreateCategoryCommand);
            var cancelCategory = UI.Button(
                UI.T("Overlay.Cancel", "Cancel"),
                () =>
                {
                    wizard.NewCategoryName = string.Empty;
                    categoryEditor.Visibility = Visibility.Collapsed;
                    addCategory.Visibility = Visibility.Visible;
                },
                ButtonKind.Ghost);
            categoryEditor.Children.Add(newCategory.Margin(0, 0, 8, 0).At(0, 0));
            categoryEditor.Children.Add(createCategory.Margin(0, 0, 6, 0).At(0, 1));
            categoryEditor.Children.Add(cancelCategory.At(0, 2));
            addCategory.Click += (_, _) =>
            {
                categoryBeforeCreate = wizard.SelectedCategoryId;
                addCategory.Visibility = Visibility.Collapsed;
                categoryEditor.Visibility = Visibility.Visible;
                newCategory.Focus(FocusState.Programmatic);
            };

            lifetime.Add(Observe.Props(wizard, () =>
            {
                if (!string.Equals(newCategory.Text, wizard.NewCategoryName, StringComparison.Ordinal))
                {
                    synchronizingNewCategory = true;
                    newCategory.Text = wizard.NewCategoryName;
                    synchronizingNewCategory = false;
                }

                if (categoryEditor.Visibility == Visibility.Visible
                    && string.IsNullOrWhiteSpace(wizard.NewCategoryName)
                    && wizard.SelectedCategoryId is not null
                    && !string.Equals(wizard.SelectedCategoryId, categoryBeforeCreate, StringComparison.Ordinal))
                {
                    categoryEditor.Visibility = Visibility.Collapsed;
                    addCategory.Visibility = Visibility.Visible;
                }
            }, nameof(ImportWizardViewModel.NewCategoryName), nameof(ImportWizardViewModel.SelectedCategoryId)));

            var categoryRow = UI.Wrap(
                6,
                category.At(0, 0),
                addCategory.Margin(8, 0, 0, 0)
                    .Align(HorizontalAlignment.Right, VerticalAlignment.Center)
                    .At(0, 1));

            var tags = new VariableWrap(6);
            void FillTags()
            {
                tags.Children.Clear();
                foreach (var tag in wizard.SelectedTags)
                {
                    tags.Children.Add(UI.Chip(
                        $"{tag.Name}  ×",
                        true,
                        () => wizard.RemoveTagCommand.Execute(tag)));
                }

                if (tags.Children.Count == 0)
                {
                    tags.Children.Add(UI.Text(
                        UI.T("Import.Tags.Empty", "No tags selected."),
                        "caption",
                        "textMuted"));
                }
            }
            lifetime.Add(Observe.Collection(wizard.SelectedTags, FillTags));

            var tagInput = new TextBox
            {
                PlaceholderText = UI.T(
                    "Import.TagPlaceholder",
                    "Type a tag. Press Enter or comma to add."),
                Text = wizard.TagSearchText,
                HorizontalAlignment = HorizontalAlignment.Stretch,
            };
            var synchronizingTagInput = false;
            tagInput.TextChanged += (_, _) =>
            {
                if (!synchronizingTagInput)
                {
                    wizard.TagSearchText = tagInput.Text;
                }
            };
            tagInput.KeyDown += (_, e) =>
            {
                if (e.Key == Windows.System.VirtualKey.Enter)
                {
                    e.Handled = true;
                    TaskObserver.Observe(
                        wizard.OnTagInputEnterAsync(),
                        "ImportSurface.OnTagInputEnterAsync");
                }
            };
            lifetime.Add(Observe.Props(wizard, () =>
            {
                if (string.Equals(tagInput.Text, wizard.TagSearchText, StringComparison.Ordinal))
                {
                    return;
                }

                synchronizingTagInput = true;
                tagInput.Text = wizard.TagSearchText;
                synchronizingTagInput = false;
            }, nameof(ImportWizardViewModel.TagSearchText)));

            var suggestions = new VariableWrap(6);
            void FillSuggestions()
            {
                suggestions.Children.Clear();
                foreach (var suggestion in wizard.TagSuggestions.Take(8))
                {
                    suggestions.Children.Add(UI.Chip(
                        suggestion.Name,
                        onClick: () => wizard.AddTagCommand.Execute(suggestion)));
                }
            }
            lifetime.Add(Observe.Collection(wizard.TagSuggestions, FillSuggestions));

            var classification = DetailLayout.Section(
                UI.T("Import.Verify.Organize", "Organization"),
                UI.T("Import.Verify.Organize.Description", "Category and tags make this Profile easier to browse and filter later."),
                "icon.profile.category",
                UI.V(5, UI.Text(UI.T("Import.Passport.CategoryLabel", "Category"), "control"), categoryRow, categoryEditor),
                UI.V(5, UI.Text(UI.T("Import.Passport.TagsLabel", "Tags"), "control"), tags, tagInput, suggestions));

            var rating = UI.H(4);
            var ratingIcons = new List<IconView>();
            for (var i = 1; i <= 5; i++)
            {
                var value = i;
                rating.Children.Add(UI.IconButton(
                    "icon.profile.rating",
                    UI.F("Import.Rating.Value", "{0} stars", value),
                    () => wizard.Rating = wizard.Rating == value ? 0 : value,
                    32));
                if (rating.Children[^1] is Button button && button.Content is IconView icon)
                {
                    ratingIcons.Add(icon);
                }
            }
            var ratingValue = UI.Text(string.Empty, "caption", "textSecondary");
            lifetime.Add(Observe.Props(wizard, () =>
            {
                for (var index = 0; index < ratingIcons.Count; index++)
                {
                    ratingIcons[index].ColorToken = wizard.Rating >= index + 1
                        ? "warning"
                        : "textMuted";
                }

                ratingValue.Text = wizard.Rating == 0
                    ? UI.T("Import.Rating.None", "Not rated")
                    : UI.F("Import.Rating.Current", "{0} of 5", wizard.Rating);
            }, nameof(ImportWizardViewModel.Rating)));

            var favorite = new ToggleSwitch
            {
                IsOn = wizard.IsFavorite,
                OnContent = UI.T("Import.Favorite.On", "Favorite"),
                OffContent = UI.T("Import.Favorite.Off", "Not favorite"),
            };
            var synchronizingFavorite = false;
            favorite.Toggled += (_, _) =>
            {
                if (!synchronizingFavorite)
                {
                    wizard.IsFavorite = favorite.IsOn;
                }
            };
            lifetime.Add(Observe.Props(wizard, () =>
            {
                synchronizingFavorite = true;
                favorite.IsOn = wizard.IsFavorite;
                synchronizingFavorite = false;
            }, nameof(ImportWizardViewModel.IsFavorite)));

            var preferenceGrid = UI.FormColumns(
                UI.V(
                        5,
                        UI.Text(UI.T("Import.Passport.RatingLabel", "RATING"), "micro", "textMuted"),
                        rating,
                        ratingValue)
                    .At(0, 0),
                UI.V(
                        5,
                        UI.Text(UI.T("Import.Passport.FavoriteLabel", "FAVORITE"), "micro", "textMuted"),
                        favorite)
                    .At(0, 1));

            var overview = new TextBox
            {
                PlaceholderText = UI.T(
                    "Import.Overview.Placeholder",
                    "Write a short description for this Profile."),
                Text = wizard.Overview,
                AcceptsReturn = true,
                TextWrapping = TextWrapping.Wrap,
                VerticalContentAlignment = VerticalAlignment.Top,
                MinHeight = 80,
                HorizontalAlignment = HorizontalAlignment.Stretch,
            };
            var synchronizingOverview = false;
            overview.TextChanged += (_, _) =>
            {
                if (!synchronizingOverview)
                {
                    wizard.Overview = overview.Text;
                }
            };
            lifetime.Add(Observe.Props(wizard, () =>
            {
                if (string.Equals(overview.Text, wizard.Overview, StringComparison.Ordinal))
                {
                    return;
                }

                synchronizingOverview = true;
                overview.Text = wizard.Overview;
                synchronizingOverview = false;
            }, nameof(ImportWizardViewModel.Overview)));

            var details = DetailLayout.Section(
                UI.T("Import.Details", "Details"),
                UI.T("Import.Details.Description", "Rating, favorite state and Overview are personal Profile details."),
                "icon.profile.person",
                preferenceGrid,
                UI.V(5, UI.Text(UI.T("Import.Passport.OverviewLabel", "Overview"), "control"), overview));

            body.Children.Add(new Border
            {
                Background = theme.Brush("surface1"),
                BorderBrush = theme.Brush("borderSubtle"),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(12),
                Padding = new Thickness(12),
                Child = document,
            });
            body.Children.Add(new ResponsiveForm(246, 8, classification, details));
        }
        else
        {
            document.Children.Add(UI.Divider());
            document.Children.Add(UI.Guidance(
                UI.T("Import.ExistingProfile.Title", "Existing Profile"),
                wizard.DestinationNote));
        }

        if (!wizard.ShowNewProfileDetails)
        {
            body.Children.Add(new Border
            {
                Background = theme.Brush("surface1"),
                BorderBrush = theme.Brush("borderSubtle"),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(12),
                Padding = new Thickness(12),
                Child = document,
            });
        }

        var problems = UI.V(8);
        void RenderProblems()
        {
            problems.Children.Clear();
            problems.Visibility = wizard.HasProblems ? Visibility.Visible : Visibility.Collapsed;
            if (!wizard.HasProblems)
            {
                return;
            }

            problems.Children.Add(UI.V(
                2,
                UI.Text(UI.T("Import.Problems", "Review required"), "body-strong"),
                UI.Text(
                    UI.T("Import.Problems.Description", "Resolve these items before this import can be added to Vault."),
                    "caption",
                    "textSecondary",
                    3)));

            if (wizard.HasPreparationPending)
            {
                problems.Children.Add(UI.Surface(UI.V(6,
                    UI.Text(UI.T("Import.PreparationRetrying", "Preparing media again…"), "body-strong"),
                    UI.Text(UI.T(
                        "Import.PreparationRetryingDetail",
                        "naut is preparing the media again. You can continue when it is ready."),
                        "caption",
                        maxLines: 3)),
                    Material.Raised, 12, 12));
            }

            foreach (var problem in wizard.PreparationProblems)
            {
                problems.Children.Add(UI.Surface(UI.V(6,
                    UI.Text(UI.T("Import.PreparationFailed", "Some media needs attention"), "body-strong"),
                    UI.Text(problem.SourceFileName, "caption", maxLines: 2),
                    UI.Text(
                        UI.T(
                            "Import.PreparationFailedDetail",
                            "naut could not prepare everything needed for this import. Try again or cancel the import."),
                        "caption",
                        "textSecondary",
                        3),
                    UI.H(8,
                        UI.Button(
                            UI.T("Import.PreparationRetry", "Try again"),
                            null,
                            ButtonKind.Primary,
                            command: wizard.RetryPreparationCommand,
                            parameter: problem))),
                    Material.Raised, 12, 12));
            }

            foreach (var problem in wizard.DuplicateProblems)
            {
                var owner = string.IsNullOrWhiteSpace(problem.ExistingProfileName)
                    ? UI.T("Import.ExistingLibraryMedia", "Existing Vault media")
                    : problem.ExistingProfileName;
                problems.Children.Add(UI.Surface(UI.V(6,
                    GatePreview(problem.PreviewImagePath),
                    UI.Text(UI.T("Import.AlreadyInLibrary", "Already in Vault"), "body-strong"),
                    UI.Text($"{problem.SourceFileName} · {owner}", "caption", maxLines: 2),
                    UI.Wrap(6,
                        UI.Button(UI.T("Import.Duplicate.Continue", "Continue in destination"), null, ButtonKind.Primary, command: wizard.ResolveDuplicateReuseCommand, parameter: problem),
                        UI.Button(UI.T("Import.Duplicate.Remove", "Remove from this import"), null, ButtonKind.Ghost, command: wizard.ResolveDuplicateSkipCommand, parameter: problem))),
                    Material.Raised, 12, 12));
            }

            foreach (var problem in wizard.ProfileCollisionProblems)
            {
                problems.Children.Add(UI.Surface(UI.V(6,
                    GatePreview(problem.PreviewImagePath),
                    UI.Text(UI.T("Import.ProfileCollision", "Recognized as another Profile"), "body-strong"),
                    UI.Text($"{problem.SourceFileName} · Strong match: {problem.CandidateProfileName}", "caption", maxLines: 2),
                    UI.Wrap(6,
                        UI.Button($"Continue {wizard.DestinationDisplayName}", null, ButtonKind.Primary, command: wizard.KeepCollisionCommand, parameter: problem),
                        UI.Button($"Move to {problem.CandidateProfileName}", null, ButtonKind.Secondary, command: wizard.MoveCollisionCommand, parameter: problem),
                        UI.Button(UI.T("Import.Collision.Skip", "Cancel this media"), null, ButtonKind.Ghost, command: wizard.SkipCollisionCommand, parameter: problem))),
                    Material.Raised, 12, 12));
            }
        }
        lifetime.Add(Observe.Collection(wizard.DuplicateProblems, RenderProblems));
        lifetime.Add(Observe.Collection(wizard.ProfileCollisionProblems, RenderProblems));
        lifetime.Add(Observe.Collection(wizard.PreparationProblems, RenderProblems));
        lifetime.Add(Observe.Props(
            wizard,
            RenderProblems,
            nameof(ImportWizardViewModel.HasProblems),
            nameof(ImportWizardViewModel.HasPreparationPending)));
        body.Children.Add(problems);

        body.Children.Add(UI.Guidance(
            UI.T("Import.Transfer.Title", "Import behavior"),
            wizard.TransferText));
    }

    private static FrameworkElement GatePreview(string? path) => string.IsNullOrWhiteSpace(path)
        ? UI.Text(UI.T("Import.PreviewUnavailable", "Preview unavailable"), "caption")
        : new SkImageView { Source = ImageRef.FromPath(path, ImportWizardViewModel.PreviewDecodeWidth), CornerRadiusValue = 10, Height = 120 };

    private Action VisualPickerDialog(
        ImportWizardViewModel wizard,
        bool isCover,
        Disposables lifetime)
    {
        var theme = ThemeRuntime.Current;
        FrameworkElement? dialog = null;
        ScrollViewer? contentOwner = null;
        var previewObservers = new List<(ImportVisualOption Option, PropertyChangedEventHandler Handler)>();
        var list = new VariableWrap(10);
        var status = UI.WrappedText(string.Empty, "caption", "textSecondary");
        var actions = new VariableWrap(6);

        var description = isCover
            ? UI.T("Import.Cover.Description", "Used as the Profile portrait and card image.")
            : UI.T("Import.Banner.Description", "Used as the moving Profile header when a prepared video is available.");

        var header = UI.Grid(
            "auto",
            "*,auto",
            UI.V(
                    1,
                    UI.Text(
                        isCover
                            ? UI.T("Import.Picker.CoverTitle", "Choose Cover")
                            : UI.T("Import.Picker.BannerTitle", "Choose Banner"),
                        "section-title"),
                    UI.Text(description, "caption", "textSecondary", 2))
                .At(0, 0),
            UI.Button(
                    UI.T("Import.Close", "Close"),
                    Close,
                    ButtonKind.Ghost,
                    "icon.action.close")
                .Align(HorizontalAlignment.Right, VerticalAlignment.Top)
                .At(0, 1));

        var scroll = UI.Scroll(list);
        var content = UI.Grid(
            "auto,auto,auto,*,auto",
            "*",
            header.At(0),
            UI.Divider().At(1),
            status.At(2),
            scroll.At(3),
            actions.At(4));
        content.RowSpacing = 12;
        content.MinWidth = 0;
        content.MaxWidth = 632;
        scroll.MaxHeight = 320;
        scroll.ViewChanged += (_, _) => HoverVideoCoordinator.Shared.StopAll();

        void UpdateEnvelope()
        {
            if (dialog is null) return;
            var height = Math.Min(520, Math.Max(0, dialog.ActualHeight - 24)) - 30;
            var reserved = header.ActualHeight + status.ActualHeight + actions.ActualHeight + 60;
            scroll.MaxHeight = Math.Max(0, Math.Min(320, height - reserved));
        }

        void Close()
        {
            HoverVideoCoordinator.Shared.StopAll();
            if (dialog is null) return;
            var current = dialog;
            dialog = null;
            if (ReferenceEquals(_visualPickerDialog, current))
            {
                _visualPickerDialog = null;
                _closeVisualPicker = null;
            }
            if (contentOwner is not null) contentOwner.Content = null;
            contentOwner = null;
            Services.Root?.Dialogs.Close(current);
        }

        void Show()
        {
            CloseVisualPicker();
            RenderChoices();
            if (Services.Root is null) return;
            dialog = Services.Root.Dialogs.Show(content, 680, Close, dismissOnScrim: true);
            contentOwner = content.Parent as ScrollViewer;
            _visualPickerDialog = dialog;
            _closeVisualPicker = Close;
            dialog.SizeChanged += (_, _) => UpdateEnvelope();
            UpdateEnvelope();
        }
        header.SizeChanged += (_, _) => UpdateEnvelope();
        status.SizeChanged += (_, _) => UpdateEnvelope();
        actions.SizeChanged += (_, _) => UpdateEnvelope();

        void DetachPreviewObservers()
        {
            foreach (var (option, handler) in previewObservers)
            {
                option.PropertyChanged -= handler;
            }
            previewObservers.Clear();
        }

        void RenderChoices()
        {
            HoverVideoCoordinator.Shared.StopAll();
            DetachPreviewObservers();
            list.Children.Clear();
            actions.Children.Clear();

            var options = (isCover ? wizard.CoverOptions : wizard.BannerOptions).ToArray();
            var selected = isCover ? wizard.SelectedCover : wizard.SelectedBanner;
            var select = isCover ? wizard.SelectCoverCommand : wizard.SelectBannerCommand;
            var pending = isCover ? wizard.IsCoverPending : wizard.IsBannerPending;
            var hasNoOptions = isCover ? wizard.HasNoCoverOptions : wizard.HasNoBannerOptions;
            var hasWeakOptions = isCover ? wizard.HasWeakCoverOptions : wizard.HasWeakBannerOptions;

            status.Visibility = Visibility.Collapsed;
            scroll.Visibility = Visibility.Visible;

            if (pending && options.Length == 0)
            {
                status.Text = isCover
                    ? UI.T("Import.PreparingCover", "Preparing Cover suggestions…")
                    : UI.T("Import.PreparingBanner", "Preparing Banner suggestions…");
                status.Visibility = Visibility.Visible;
                scroll.Visibility = Visibility.Collapsed;
            }
            else if (hasNoOptions)
            {
                status.Text = isCover
                    ? UI.T("Import.NoCoverSuggestion", "No suitable Cover suggestion was prepared.")
                    : UI.T("Import.NoBannerSuggestion", "No suitable Banner suggestion was prepared.");
                status.Visibility = Visibility.Visible;
                scroll.Visibility = Visibility.Collapsed;
            }
            else
            {
                if (hasWeakOptions)
                {
                    status.Text = UI.T(
                        "Import.Appearance.NoStrongRecommendation",
                        "No strong recommendation. Choose any eligible media.");
                    status.Visibility = Visibility.Visible;
                }

                var imageWidth = isCover ? 142d : 180d;
                const double imageHeight = 96;
                foreach (var option in options)
                {
                    var image = new SkImageView
                    {
                        Source = option.Preview,
                        CornerRadiusValue = 9,
                        Width = imageWidth,
                        Height = imageHeight,
                    };
                    var mediaHost = new Grid { Width = imageWidth, Height = imageHeight };
                    mediaHost.Children.Add(image);

                    PropertyChangedEventHandler previewObserver = (_, args) =>
                    {
                        if (args.PropertyName is nameof(ImportVisualOption.Preview) or nameof(ImportVisualOption.HasPreview))
                        {
                            UiDispatch.Run(() => image.Source = option.Preview);
                        }
                    };
                    option.PropertyChanged += previewObserver;
                    previewObservers.Add((option, previewObserver));

                    var labels = new VariableWrap(4);
                    if (option.IsRecommended)
                    {
                        labels.Children.Add(UI.Badge(UI.T("Import.Recommended", "Recommended"), "accent"));
                    }
                    if (option.IsSelected)
                    {
                        labels.Children.Add(UI.Badge(UI.T("Import.Selected", "Selected"), "success"));
                    }

                    var tile = new Button
                    {
                        Content = UI.V(
                            5,
                            mediaHost,
                            UI.Text(option.Title, "caption", maxLines: 1),
                            labels),
                        Width = imageWidth + 16,
                        Padding = new Thickness(6),
                        CornerRadius = new CornerRadius(12),
                        Background = theme.Brush(option.IsSelected ? "surface2" : "surface1"),
                        BorderBrush = theme.Brush(option.IsSelected ? "accent" : "borderSubtle"),
                        BorderThickness = new Thickness(2),
                        HorizontalContentAlignment = HorizontalAlignment.Stretch,
                        VerticalContentAlignment = VerticalAlignment.Top,
                    };
                    Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(
                        tile,
                        option.IsSelected
                            ? UI.F("Import.Appearance.SelectedAutomation", "{0}, selected", option.Title)
                            : option.Title);
                    tile.Click += (_, _) =>
                    {
                        HoverVideoCoordinator.Shared.StopAll();
                        select.Execute(option);
                        Close();
                    };
                    tile.PointerEntered += (_, _) =>
                    {
                        if (option.MotionPath is { } motion)
                        {
                            HoverVideoCoordinator.Shared.Play(mediaHost, motion);
                        }
                    };
                    tile.PointerExited += (_, _) => HoverVideoCoordinator.Shared.Release(mediaHost);
                    list.Children.Add(tile);
                }
            }

            if (selected is not null)
            {
                actions.Children.Add(UI.Button(
                    UI.T("Import.Appearance.Clear", "Clear selection"),
                    () =>
                    {
                        (isCover ? wizard.ClearCoverCommand : wizard.ClearBannerCommand).Execute(null);
                        Close();
                    },
                    ButtonKind.Ghost));
            }
        }

        lifetime.Add(Observe.Collection(isCover ? wizard.CoverOptions : wizard.BannerOptions, RenderChoices));
        lifetime.Add(Observe.Props(
            wizard,
            RenderChoices,
            isCover ? nameof(ImportWizardViewModel.SelectedCover) : nameof(ImportWizardViewModel.SelectedBanner),
            isCover ? nameof(ImportWizardViewModel.IsCoverPending) : nameof(ImportWizardViewModel.IsBannerPending),
            isCover ? nameof(ImportWizardViewModel.HasNoCoverOptions) : nameof(ImportWizardViewModel.HasNoBannerOptions),
            isCover ? nameof(ImportWizardViewModel.HasWeakCoverOptions) : nameof(ImportWizardViewModel.HasWeakBannerOptions)));
        lifetime.Add(DetachPreviewObservers);
        RenderChoices();
        lifetime.Add(Close);
        return Show;
    }

    private void CloseVisualPicker()
    {
        _closeVisualPicker?.Invoke();
    }

    public override void Dispose()
    {
        CloseVisualPicker();
        _wizardRenderBag?.Dispose();
        _wizardBag?.Dispose();
        RetireAllRows();
        base.Dispose();
    }
}
