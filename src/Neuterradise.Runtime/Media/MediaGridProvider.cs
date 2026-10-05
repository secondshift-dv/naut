using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Neuterradise.App.Profiles;
using Neuterradise.App.Shell;
using Neuterradise.App.SystemServices.Database;

namespace Neuterradise.App.Media;

public sealed class MediaGridProvider
{
    public const int DefaultPageSize = 96;
    public const int MaxPageSize = 192;

    private readonly CatalogDb _catalog;

    public MediaGridProvider(CatalogDb catalog)
    {
        _catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));
    }

    public static int ClampPageSize(int requested) =>
        requested <= 0 ? DefaultPageSize : Math.Min(requested, MaxPageSize);

    public async Task<MediaGridPage> GetProfileMediaAsync(
        MediaGridQuery query,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);

        if (query.ProfileId == Guid.Empty)
        {
            throw new ArgumentException("A Profile Media query needs a ProfileId.", nameof(query));
        }

        var page = await _catalog.ProfileReads.GetMediaPageAsync(
            query.ProfileId,
            ToProfileMediaFilter(query.Relation),
            ToMediaType(query.Type),
            ClampPageSize(query.PageSize),
            query.Cursor,
            cancellationToken,
            isFavoriteOnly: query.IsFavoriteOnly,
            sort: query.Sort).ConfigureAwait(false);

        var items = new List<MediaGridItem>(page.Items.Count);
        foreach (var row in page.Items)
        {
            items.Add(new MediaGridItem(
                row.MediaId,
                row.MediaType,
                ToRelationBadge(row.RelationType),
                row.ManagedFileName,
                row.PixelWidth,
                row.PixelHeight,
                row.DurationMs,
                HasAttention: row.PathState != ManagedPathState.None,
                IsFavorite: row.IsFavorite,
                DisplayFileName: row.OriginalFileName,
                AddedToLibraryAtUtc: row.AddedToLibraryAtUtc));
        }

        return new MediaGridPage(items, page.NextPageToken, page.HasMore);
    }

    public async Task<(Guid? PreviousMediaId, Guid? NextMediaId)> GetAdjacentMediasAsync(
        Guid currentMediaId,
        MediaOriginState? origin,
        CancellationToken cancellationToken = default)
    {
        if (origin is null || origin.ProfileId == Guid.Empty)
        {
            return (null, null);
        }

        var relation = Enum.TryParse<MediaRelationFilter>(origin.RelationFilter, ignoreCase: true, out var r)
            ? r : MediaRelationFilter.All;
        var type = Enum.TryParse<MediaTypeFilter>(origin.TypeFilter, ignoreCase: true, out var t)
            ? t : MediaTypeFilter.All;

        // Keep adjacency inside the exact originating dataset. MediaOriginState is the durable
        // navigation context, so favorite-only must be carried just like relation/type/sort.
        var sort = Enum.TryParse<MediaGridSort>(origin.Sort, ignoreCase: true, out var s)
            ? s : MediaGridSort.NewestFirst;

        return await _catalog.ProfileReads.GetAdjacentMediaIdsAsync(
            origin.ProfileId,
            currentMediaId,
            ToProfileMediaFilter(relation),
            ToMediaType(type),
            isFavoriteOnly: origin.IsFavoriteOnly,
            sort: sort,
            cancellationToken: cancellationToken).ConfigureAwait(false);
    }

    private static ProfileMediaFilter ToProfileMediaFilter(MediaRelationFilter filter) => filter switch
    {
        MediaRelationFilter.Owned => ProfileMediaFilter.Owned,
        MediaRelationFilter.AppearsIn => ProfileMediaFilter.AppearsIn,
        MediaRelationFilter.Manual => ProfileMediaFilter.Manual,
        _ => ProfileMediaFilter.All,
    };

    private static MediaType? ToMediaType(MediaTypeFilter filter) => filter switch
    {
        MediaTypeFilter.Images => MediaType.Image,
        MediaTypeFilter.Videos => MediaType.Video,
        MediaTypeFilter.Models => MediaType.Model,
        _ => null,
    };

    private static MediaRelationBadge ToRelationBadge(ProfileMediaRelation relation) => relation switch
    {
        ProfileMediaRelation.Owner => MediaRelationBadge.Owned,
        ProfileMediaRelation.Appears => MediaRelationBadge.AppearsIn,
        _ => MediaRelationBadge.Manual,
    };
}
