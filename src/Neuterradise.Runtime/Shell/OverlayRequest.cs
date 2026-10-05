using Neuterradise.App.Localization;
using Neuterradise.App.Presentation;
namespace Neuterradise.App.Shell;

public enum OverlayOutcome
{
    Confirmed,
    Dismissed,
}

public abstract record OverlayRequest
{
    private protected OverlayRequest(
        string title,
        bool blocksBackgroundInput = true,
        bool isDismissableByEscape = true)
    {
        if (string.IsNullOrWhiteSpace(title))
        {
            throw new ArgumentException("An overlay request needs a title.", nameof(title));
        }

        Title = title;
        BlocksBackgroundInput = blocksBackgroundInput;
        IsDismissableByEscape = isDismissableByEscape;
    }

    public string Title { get; }
    public bool BlocksBackgroundInput { get; }
    public bool IsDismissableByEscape { get; }
}

public sealed record ConfirmationOverlayRequest : OverlayRequest
{
    public ConfirmationOverlayRequest(
        string title,
        string message,
        string confirmLabel,
        string? cancelLabel = null,
        bool isDestructive = false,
        string? destructiveActionName = null,
        bool isDismissableByEscape = true)
        : base(title, blocksBackgroundInput: true, isDismissableByEscape: isDismissableByEscape)
    {
        if (string.IsNullOrWhiteSpace(message))
        {
            throw new ArgumentException("A confirmation needs a message.", nameof(message));
        }

        if (string.IsNullOrWhiteSpace(confirmLabel))
        {
            throw new ArgumentException("A confirmation needs a confirm label.", nameof(confirmLabel));
        }

        if (isDestructive && string.IsNullOrWhiteSpace(destructiveActionName))
        {
            throw new ArgumentException(
                "A destructive confirmation must name the irreversible action.",
                nameof(destructiveActionName));
        }

        Message = message;
        ConfirmLabel = confirmLabel;
        CancelLabel = string.IsNullOrWhiteSpace(cancelLabel) ? SurfaceText.Get("Overlay.Cancel", "Cancel") : cancelLabel;
        IsDestructive = isDestructive;
        DestructiveActionName = isDestructive ? destructiveActionName : null;
    }

    public string Message { get; }
    public string ConfirmLabel { get; }
    public string CancelLabel { get; }
    public bool IsDestructive { get; }
    public string? DestructiveActionName { get; }

    public string? FormattedDestructiveWarning => IsDestructive && !string.IsNullOrWhiteSpace(DestructiveActionName)
        ? SurfaceText.Format("Overlay.DestructiveWarning", "This will {0}. It cannot be undone.", DestructiveActionName)
        : null;
}

public sealed record ErrorDetailOverlayRequest : OverlayRequest
{
    public ErrorDetailOverlayRequest(
        string message,
        string? detail = null,
        string? title = null)
        : base(string.IsNullOrWhiteSpace(title) ? SurfaceText.Get("Overlay.SomethingWentWrong", "Something went wrong") : title, blocksBackgroundInput: true, isDismissableByEscape: true)
    {
        if (string.IsNullOrWhiteSpace(message))
        {
            throw new ArgumentException("An error overlay needs a message.", nameof(message));
        }

        Message = message;
        Detail = string.IsNullOrWhiteSpace(detail) ? null : detail;
    }

    public string Message { get; }
    public string? Detail { get; }
}

public enum ProfileCustomizationSection
{
    ProfileLayout,
    MediaLayout,
    ProfileAppearance,
}

/// <summary>Transient launch envelope only. Editable Profile presentation state lives in PreviewSession.</summary>
public sealed record ProfileCustomizationOverlayRequest : OverlayRequest
{
    public ProfileCustomizationOverlayRequest(
        ProfilePresentationState profileState,
        string profileDisplayName,
        ProfileCustomizationSection initialSection = ProfileCustomizationSection.ProfileLayout,
        string? title = null)
        : base(
            string.IsNullOrWhiteSpace(title)
                ? SurfaceText.Get("Profile.Customize", "Customize Profile")
                : title,
            blocksBackgroundInput: true,
            isDismissableByEscape: true)
    {
        ArgumentNullException.ThrowIfNull(profileState);
        ProfileState = profileState;
        ProfileDisplayName = profileDisplayName ?? string.Empty;
        InitialSection = initialSection;
    }

    public ProfilePresentationState ProfileState { get; }

    public string ProfileDisplayName { get; }

    public ProfileCustomizationSection InitialSection { get; }
}

public sealed record ProfilePickerItem(Guid ProfileId, string DisplayName, string? CategoryName);

public sealed record ProfilePickerOverlayRequest : OverlayRequest
{
    public ProfilePickerOverlayRequest(
        IReadOnlyList<ProfilePickerItem> candidates,
        Action<ProfilePickerItem> onProfileSelected,
        string? title = null,
        string? prompt = null,
        string? initialSearchText = null)
        : base(
            string.IsNullOrWhiteSpace(title)
                ? SurfaceText.Get("SurfaceText.Select.Existing.Profile.7B23D2D3", "Choose a profile")
                : title,
            blocksBackgroundInput: true,
            isDismissableByEscape: true)
    {
        Candidates = candidates ?? [];
        OnProfileSelected = onProfileSelected ?? throw new ArgumentNullException(nameof(onProfileSelected));
        Prompt = string.IsNullOrWhiteSpace(prompt)
            ? SurfaceText.Get("SurfaceText.Select.Existing.Profile.7B23D2D3", "Choose a profile")
            : prompt;
        InitialSearchText = initialSearchText?.Trim() ?? string.Empty;
    }

    public string Prompt { get; }
    public string InitialSearchText { get; }
    public IReadOnlyList<ProfilePickerItem> Candidates { get; }
    public Action<ProfilePickerItem> OnProfileSelected { get; }
    public ProfilePickerItem? SelectedCandidate { get; set; }
}

public sealed record ProfileAssociationEntry(Guid TargetProfileId, string DisplayName, string RelationKind);

public sealed record AssociationEditorOverlayRequest : OverlayRequest
{
    public AssociationEditorOverlayRequest(
        Guid sourceProfileId,
        string sourceProfileDisplayName,
        IReadOnlyList<ProfileAssociationEntry> associations,
        Action<Guid>? onRemoveAssociation = null,
        Action? onAddAssociationRequested = null,
        string? title = null)
        : base(
            string.IsNullOrWhiteSpace(title)
                ? SurfaceText.Get("Profile.Related", "Related")
                : title,
            blocksBackgroundInput: true,
            isDismissableByEscape: true)
    {
        SourceProfileId = sourceProfileId;
        SourceProfileDisplayName = sourceProfileDisplayName;
        Associations = associations ?? [];
        OnRemoveAssociation = onRemoveAssociation;
        OnAddAssociationRequested = onAddAssociationRequested;
    }

    public Guid SourceProfileId { get; }
    public string SourceProfileDisplayName { get; }
    public IReadOnlyList<ProfileAssociationEntry> Associations { get; }
    public Action<Guid>? OnRemoveAssociation { get; }
    public Action? OnAddAssociationRequested { get; }
}
