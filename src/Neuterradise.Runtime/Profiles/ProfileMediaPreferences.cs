using Neuterradise.App.SystemServices.Database;

namespace Neuterradise.App.Profiles;

internal static class ProfileMediaPreferences
{
    internal static string FavoritesKey(Guid profileId) => $"profile.media-grid.favorites-only.{DbGuid.Format(profileId)}";

    internal static async Task<bool> ReadFavoritesAsync(CatalogDb catalog, Guid profileId, CancellationToken cancellationToken)
    {
        // A reopened route must see toggle writes already admitted by the catalog coordinator.
        await using var lease = await catalog.WriteCoordinator.EnterAsync(cancellationToken).ConfigureAwait(false);
        return await ReadFavoritesForSnapshotAsync(catalog, profileId, cancellationToken).ConfigureAwait(false);
    }

    internal static async Task<bool> ReadFavoritesForSnapshotAsync(CatalogDb catalog, Guid profileId, CancellationToken cancellationToken)
    {
        var value = await catalog.SettingsReads.GetSettingValueAsync(FavoritesKey(profileId), cancellationToken).ConfigureAwait(false);
        return string.Equals(value?.Trim(' ', '\t', '\r', '\n'), "true", StringComparison.Ordinal);
    }
}
