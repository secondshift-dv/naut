using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Neuterradise.App.Media;
using Neuterradise.App.Ui.Figure;

namespace Neuterradise.App.Ui;

public sealed partial class CustomizationCenter
{
    private readonly ProfileFigureView _profilePreviewFigure;
    private bool _figureEditorOpen;
    private bool _figureEnabledDraft;
    private Guid? _rememberedFigureId;
    private Guid? _figureChooserOriginalId;
    private Guid? _figureChooserOriginalRememberedId;

    private void OpenFigureEditor()
    {
        if (_saving || _session.ProfileWorking is null)
        {
            return;
        }

        _figureChooserOriginalId = _session.ProfileWorking.Sources?.FigureMediaId;
        _figureChooserOriginalRememberedId = _rememberedFigureId;
        _figureEditorOpen = true;
        _dedicatedEditor = true;
        _workspace.SetEditor(true);
        EditorChanged?.Invoke(true);
        RenderGroupTabs();
        RenderAll();
    }

    private void ReturnFromFigureEditor()
    {
        if (!_figureEditorOpen)
        {
            return;
        }

        _session.SelectFigureSource(_figureChooserOriginalId);
        _rememberedFigureId = _figureChooserOriginalRememberedId;
        _figureEnabledDraft = _session.ProfileWorking?.Sources?.FigureMediaId is not null;
        _figureEditorOpen = false;
        _dedicatedEditor = false;
        _workspace.SetEditor(false);
        EditorChanged?.Invoke(false);
        RenderGroupTabs();
        RenderAll();
    }

    private FrameworkElement FigureInspectorControl()
    {
        var body = UI.V(8);
        if (_subject is null || _session.ProfileWorking is null)
        {
            body.Children.Add(UI.Text(
                UI.T("Customize.Figure.Loading", "Loading 3D Figure options…"),
                "body-muted"));
            return UI.Section(
                UI.T("Customize.Figure.Title", "3D Figure"),
                UI.T("Customize.Figure.Description", "Show a 3D figure on this Profile."),
                body);
        }

        var selectedId = _session.ProfileWorking.Sources?.FigureMediaId;
        var selected = selectedId is { } id
            ? _subject.FigureCandidates.FirstOrDefault(candidate => candidate.MediaId == id)
            : null;
        var selectable = _subject.FigureCandidates.Where(static candidate => candidate.IsSelectable).ToList();

        var toggle = new ToggleSwitch
        {
            IsOn = selectedId is not null || _figureEnabledDraft,
            IsEnabled = selectedId is not null || _subject.FigureCandidates.Count > 0,
            HorizontalAlignment = HorizontalAlignment.Stretch,
        };
        toggle.Toggled += (_, _) =>
        {
            if (toggle.IsOn)
            {
                _figureEnabledDraft = true;
                if (_session.ProfileWorking?.Sources?.FigureMediaId is not null)
                {
                    return;
                }

                var remembered = selectable.FirstOrDefault(candidate => candidate.MediaId == _rememberedFigureId);
                if (remembered is not null)
                {
                    SelectFigureCandidate(remembered);
                    return;
                }

                if (selectable.Count == 1)
                {
                    SelectFigureCandidate(selectable[0]);
                    return;
                }

                OpenFigureEditor();
                return;
            }

            _rememberedFigureId = _session.ProfileWorking?.Sources?.FigureMediaId ?? _rememberedFigureId;
            _figureEnabledDraft = false;
            _session.SelectFigureSource(null);
            UpdateFigurePreviewSource();
        };
        body.Children.Add(toggle);

        if (_subject.FigureCandidates.Count == 0)
        {
            body.Children.Add(UI.Guidance(
                UI.T("Customize.Figure.NoCandidateTitle", "No 3D Figure available"),
                UI.T("Customize.Figure.NoCandidate", "Add a supported 3D model to this Profile first."),
                "warning"));
        }
        else if (selected is not null)
        {
            body.Children.Add(UI.Text(selected.DisplayName, "control", maxLines: 2));
            if (!selected.IsSelectable)
            {
                body.Children.Add(UI.WrappedText(FigureCandidateStatus(selected), "caption", "warning"));
            }
        }
        else if (_figureEnabledDraft && selectable.Count > 1)
        {
            body.Children.Add(UI.WrappedText(
                UI.T("Customize.Figure.ChoosePrompt", "Choose which model to show as this Profile's 3D Figure."),
                "caption",
                "textSecondary"));
        }

        if (_subject.FigureCandidates.Count > 0)
        {
            var change = UI.Button(
                selectedId is null
                    ? UI.T("Customize.Figure.Choose", "Choose 3D Figure")
                    : UI.T("Customize.Figure.Change", "Change"),
                OpenFigureEditor,
                ButtonKind.Secondary,
                "icon.navigation.customize");
            change.HorizontalAlignment = HorizontalAlignment.Stretch;
            body.Children.Add(change);
        }

        return UI.Section(
            UI.T("Customize.Figure.Title", "3D Figure"),
            UI.T("Customize.Figure.Description", "Show a 3D figure on this Profile."),
            body);
    }

    private FrameworkElement FigureEditor()
    {
        if (_subject is null)
        {
            return UI.Text(UI.T("Customize.Figure.Loading", "Loading 3D Figure options…"), "body-muted");
        }

        return FigureCandidateChooser();
    }

    private FrameworkElement FigureCandidateChooser()
    {
        if (_subject is null || _subject.FigureCandidates.Count == 0)
        {
            return UI.Guidance(
                UI.T("Customize.Figure.NoCandidateTitle", "No 3D Figure available"),
                UI.T("Customize.Figure.NoCandidate", "Add a supported 3D model to this Profile first."),
                "warning");
        }

        var selectedId = _session.ProfileWorking?.Sources?.FigureMediaId;
        var list = UI.V(6);
        foreach (var candidate in _subject.FigureCandidates)
        {
            var thumbnail = new SkImageView
            {
                Source = ImageRef.FromPath(candidate.ThumbnailPath, 320),
                Width = 72,
                Height = 72,
                CornerRadiusValue = 8,
                PlaceholderToken = "surface2",
            };
            var button = CustomizationChoiceView.Create(
                candidate.DisplayName,
                thumbnail,
                selectedId == candidate.MediaId,
                isDefault: false,
                isUser: false,
                () => SelectFigureCandidate(candidate),
                FigureCandidateStatus(candidate),
                squarePreview: true);
            button.IsEnabled = candidate.IsSelectable;
            list.Children.Add(button);

            if (!candidate.IsSelectable)
            {
                list.Children.Add(UI.WrappedText(
                    FigureCandidateStatus(candidate),
                    "caption",
                    candidate.EligibilityReason == "NEEDS_REPAIR" ? "warning" : "textSecondary"));
            }
        }

        return list;
    }

    private string FigureCandidateStatus(CustomizationFigureCandidate candidate) =>
        candidate.IsSelectable
            ? UI.T("Customize.Figure.Ready", "Ready")
            : candidate.EligibilityReason == "NEEDS_REPAIR"
                ? UI.T("Customize.Figure.NeedsRepair", "Figure needs repair.")
                : UI.T("Customize.Figure.Preparing", "Figure is being prepared.");

    private void SelectFigureCandidate(CustomizationFigureCandidate candidate)
    {
        if (!candidate.IsSelectable)
        {
            return;
        }

        _figureEnabledDraft = true;
        _rememberedFigureId = candidate.MediaId;
        _session.SelectFigureSource(candidate.MediaId);
        UpdateFigurePreviewSource();
    }

    private FrameworkElement? UpdateFigurePreviewSource()
    {
        if (_subject is null || _session.ProfileWorking?.Sources?.FigureMediaId is not { } mediaId)
        {
            _profilePreviewFigure.SetSource(null);
            return null;
        }

        var candidate = _subject.FigureCandidates.FirstOrDefault(item => item.MediaId == mediaId);
        if (candidate is null)
        {
            _profilePreviewFigure.SetSource(null);
            return null;
        }

        _profilePreviewFigure.SetSource(new FigureSource(
            candidate.MediaId,
            candidate.IsSelectable && _figureEditorOpen ? candidate.RenderPath : null,
            candidate.SourceSha256,
            ImageRef.FromPath(candidate.ThumbnailPath, 900)));
        return _profilePreviewFigure;
    }
}
