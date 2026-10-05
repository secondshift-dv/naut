using Neuterradise.App.Gallery;
using Neuterradise.App.Localization;

namespace Neuterradise.App.Shell;

public abstract record AppRoute;

public sealed record HomeRoute : AppRoute;

/// <summary>
/// Snapshot of Gallery query/pagination state at the moment the user leaves Gallery.
/// Presentation layout is owned separately by the Gallery surface presentation slot.
/// </summary>
public sealed record GalleryOriginState(
    string? SearchText,
    string? CategoryId,
    string? TagId,
    bool FavoritesOnly,
    int? MinRating,
    int? MaxRating,
    bool HasImages,
    bool HasVideos,
    bool HasModels,
    Guid? RelatedToProfileId,
    string? RelatedToProfileName,
    bool HasSharedMedia,
    GalleryProfileKindFilter ProfileKindFilter,
    GallerySortOrder SortOrder,
    int PageIndex,
    int PageSize,
    Guid? SelectedProfileId = null,
    Guid? AnchorProfileId = null,
    double AnchorOffsetDip = 0);

public sealed record GalleryRoute(GalleryOriginState? Origin = null) : AppRoute;

public sealed record ImportRoute : AppRoute;

public enum SettingsSection
{
    Appearance,
    Vault,
    PeopleOrganization,
    Activity,
    Language,
    Trash,
    About,
    Presentation,
}

public enum SettingsSubsection
{
    Categories,
    Tags,
    VaultHealth,
}

public sealed record SettingsRoute(
    SettingsSection Section = SettingsSection.Appearance,
    SettingsSubsection? Subsection = null) : AppRoute;

/// <summary>
/// A Profile page. <paramref name="InspectMediaId"/> opens the media inspector on that asset once
/// the page is shown; media detail is an inspector inside the Profile, not a destination of its own.
/// </summary>
public sealed record ProfileRoute(
    Guid ProfileId,
    MediaOriginState? Origin = null,
    Guid? InspectMediaId = null) : AppRoute;

public sealed record MediaOriginState(
    Guid ProfileId,
    string RelationFilter,
    string TypeFilter,
    string Sort,
    Guid? SelectedMediaId,
    int Page = 1,
    int PageSize = 96,
    bool IsFavoriteOnly = false);

public sealed record FaceReviewRoute(Guid? ProfileId = null) : AppRoute;

public sealed record TrashRoute : AppRoute;

public sealed record VaultHealthRoute : AppRoute;

public sealed record BreadcrumbItem(string Label, AppRoute? Target = null);

/// <summary>Single typed authority for route ownership and ancestry shown by the shell.</summary>
public static class NavigationContextResolver
{
    public static AppRoute OwningTopLevel(AppRoute route) => route switch
    {
        HomeRoute => new HomeRoute(),
        GalleryRoute or ProfileRoute => new GalleryRoute(),
        ImportRoute => new ImportRoute(),
        SettingsRoute or TrashRoute or VaultHealthRoute or FaceReviewRoute => new SettingsRoute(),
        _ => throw new NotSupportedException($"No top-level owner is defined for {route.GetType().Name}."),
    };

    public static IReadOnlyList<BreadcrumbItem> Breadcrumb(AppRoute route) => route switch
    {
        HomeRoute => [new(SurfaceText.Get("Nav.Home", "Home"))],
        GalleryRoute => [new(SurfaceText.Get("Nav.Gallery", "Gallery"))],
        ImportRoute => [new(SurfaceText.Get("Nav.Import", "Import"))],
        SettingsRoute settings => SettingsBreadcrumb(settings),
        TrashRoute => [
            new(SurfaceText.Get("Nav.Settings", "Settings"), new SettingsRoute()),
            new(SurfaceText.Get("Settings.Trash.Title", "Trash"), new SettingsRoute(SettingsSection.Trash))],
        VaultHealthRoute => [
            new(SurfaceText.Get("Nav.Settings", "Settings"), new SettingsRoute()),
            new(SurfaceText.Get("Settings.Vault.Title", "Vault"), new SettingsRoute(SettingsSection.Vault)),
            new(SurfaceText.Get("Breadcrumb.VaultHealth", "Vault Health"), new SettingsRoute(SettingsSection.Vault, SettingsSubsection.VaultHealth))],
        FaceReviewRoute => [
            new(SurfaceText.Get("Nav.Settings", "Settings"), new SettingsRoute()),
            new(SurfaceText.Get("Settings.PeopleOrganization.Title", "People & Organization"), new SettingsRoute(SettingsSection.PeopleOrganization))],
        ProfileRoute =>
            [new(SurfaceText.Get("Nav.Gallery", "Gallery"), new GalleryRoute()), new(SurfaceText.Get("Breadcrumb.Profile", "Profile"))],
        _ => [new(route.GetType().Name)],
    };

    private static IReadOnlyList<BreadcrumbItem> SettingsBreadcrumb(SettingsRoute route)
    {
        var root = new BreadcrumbItem(SurfaceText.Get("Nav.Settings", "Settings"), new SettingsRoute());
        var section = new BreadcrumbItem(
            SurfaceText.Get($"Settings.{route.Section}.Title", route.Section.ToString()),
            new SettingsRoute(route.Section));
        return route.Subsection is null
            ? [root, section]
            : [
                root,
                section,
                new BreadcrumbItem(
                    SurfaceText.Get($"Settings.{route.Subsection}.Title", SplitName(route.Subsection.Value.ToString())),
                    new SettingsRoute(route.Section, route.Subsection))];
    }

    private static string SplitName(string value) =>
        string.Concat(value.Select((character, index) => index > 0 && char.IsUpper(character) ? $" {character}" : character.ToString()));
}
