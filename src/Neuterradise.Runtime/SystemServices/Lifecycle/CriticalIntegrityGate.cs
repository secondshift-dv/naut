using Microsoft.Data.Sqlite;
using Neuterradise.App.SystemServices.Database;
using Neuterradise.App.Media;
using Neuterradise.App.Media.Model;
using Neuterradise.App.SystemServices.Storage;
using Neuterradise.App.Profiles;
using Neuterradise.App.SystemServices.Diagnostics;

namespace Neuterradise.App.SystemServices.Lifecycle;

public sealed class CriticalIntegrityGate
{
    private readonly CatalogDb _catalog;
    private readonly StructuredDiagnostics? _diagnostics;

    public CriticalIntegrityGate(CatalogDb catalog, StructuredDiagnostics? diagnostics = null)
    {
        _catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));
        _diagnostics = diagnostics;
    }

    public async Task<CriticalIntegrityResult> EvaluateAsync(CancellationToken cancellationToken = default)
    {
        var violations = new List<CriticalIntegrityViolation>();
        await using var connection = await _catalog.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await CheckActiveMediaOwnerAsync(connection, violations, cancellationToken).ConfigureAwait(false);
        await CheckNonActiveMediaOwnerAsync(connection, violations, cancellationToken).ConfigureAwait(false);
        await CheckNormalProfilesIdentityAsync(connection, violations, cancellationToken).ConfigureAwait(false);
        await CheckUnknownProfilesIdentityAsync(connection, violations, cancellationToken).ConfigureAwait(false);
        await CheckNonterminalStagingCoherenceAsync(connection, violations, cancellationToken).ConfigureAwait(false);
        await CheckManagedPathAuthorityAsync(connection, violations, cancellationToken).ConfigureAwait(false);
        await CheckActiveAppearanceReferencesAsync(connection, violations, cancellationToken).ConfigureAwait(false);
        await CheckModelRenderAuthorityAsync(connection, violations, cancellationToken).ConfigureAwait(false);
        var result = violations.Count == 0 ? CriticalIntegrityResult.Success() : CriticalIntegrityResult.Failure(violations);
        if (!result.Passed)
            foreach (var violation in result.Violations)
                _diagnostics?.Write(new DiagnosticEvent(DateTimeOffset.UtcNow, DiagnosticSeverity.Error, "lifecycle.integrity", violation.Code, StructuredDiagnostics.SanitizeDetail(violation.Message)));
        return result;
    }

    private static async Task CheckModelRenderAuthorityAsync(SqliteConnection connection,List<CriticalIntegrityViolation> violations,CancellationToken ct)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT aa.media_id, aa.relative_path, a.media_type, coalesce(a.current_managed_file_name,a.original_file_name),
                   a.dependency_status, a.media_storage_token, a.sha256
            FROM media_assets aa JOIN media a ON a.media_id = aa.media_id
            WHERE aa.role = 'MODEL_RENDER';
            """;
        await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            var valid = !reader.IsDBNull(6) && reader.GetString(2) == "MODEL" && !reader.IsDBNull(3)
                && ModelRenderEligibility.IsEligible(reader.GetString(3),reader.GetString(4));
            try { valid &= reader.GetString(1) == new ManagedPathPlanner().PlanMediaAsset(new MediaStorageToken(reader.GetString(5)),MediaAssetRole.ModelRender); }
            catch (ArgumentException) { valid = false; }
            if (!valid) violations.Add(new("MODEL_RENDER_AUTHORITY_INVALID","ModelRender source eligibility or canonical path is invalid.","Media",reader.GetString(0)));
        }
    }
    private static async Task CheckActiveMediaOwnerAsync(SqliteConnection c, List<CriticalIntegrityViolation> v, CancellationToken t)
    {
        await using var cmd = c.CreateCommand(); cmd.CommandText = """
            SELECT a.media_id, COUNT(pa.profile_id) FROM media a LEFT JOIN profile_media pa ON a.media_id=pa.media_id AND pa.relation_type='OWNER'
            WHERE a.state='ACTIVE' AND a.trashed_at_ms IS NULL GROUP BY a.media_id HAVING COUNT(pa.profile_id) != 1;
            """;
        await using var r = await cmd.ExecuteReaderAsync(t).ConfigureAwait(false);
        while (await r.ReadAsync(t).ConfigureAwait(false)) v.Add(new(r.GetInt64(1)==0 ? "ACTIVE_MEDIA_NO_OWNER" : "ACTIVE_MEDIA_MULTIPLE_OWNERS", "Active media owner cardinality is invalid.", "Media", r.GetString(0)));
    }

    private static async Task CheckNonActiveMediaOwnerAsync(SqliteConnection c, List<CriticalIntegrityViolation> v, CancellationToken t)
    {
        await using var cmd=c.CreateCommand(); cmd.CommandText="SELECT a.media_id FROM media a JOIN profile_media pa ON a.media_id=pa.media_id AND pa.relation_type='OWNER' WHERE a.state IN ('CANDIDATE','RETIRED');";
        await using var r=await cmd.ExecuteReaderAsync(t).ConfigureAwait(false); while(await r.ReadAsync(t).ConfigureAwait(false)) v.Add(new("NON_ACTIVE_MEDIA_HAS_OWNER","Non-active media has an OWNER relation.","Media",r.GetString(0)));
    }

    private static async Task CheckNormalProfilesIdentityAsync(SqliteConnection c,List<CriticalIntegrityViolation> v,CancellationToken t)
    {
        await using var cmd=c.CreateCommand(); cmd.CommandText="SELECT p.profile_id,COUNT(i.identity_id) FROM profiles p LEFT JOIN identities i ON p.profile_id=i.profile_id AND i.is_active=1 WHERE p.kind='NORMAL' AND p.trashed_at_ms IS NULL GROUP BY p.profile_id HAVING COUNT(i.identity_id)!=1;";
        await using var r=await cmd.ExecuteReaderAsync(t).ConfigureAwait(false); while(await r.ReadAsync(t).ConfigureAwait(false)) v.Add(new(r.GetInt64(1)==0?"NORMAL_PROFILE_NO_IDENTITY":"NORMAL_PROFILE_MULTIPLE_IDENTITIES","Active NORMAL identity cardinality is invalid.","Profile",r.GetString(0)));
    }

    private static async Task CheckUnknownProfilesIdentityAsync(SqliteConnection c,List<CriticalIntegrityViolation> v,CancellationToken t)
    {
        await using var cmd=c.CreateCommand(); cmd.CommandText="SELECT p.profile_id FROM profiles p JOIN identities i ON p.profile_id=i.profile_id AND i.is_active=1 WHERE p.kind='UNKNOWN' AND p.trashed_at_ms IS NULL;";
        await using var r=await cmd.ExecuteReaderAsync(t).ConfigureAwait(false); while(await r.ReadAsync(t).ConfigureAwait(false)) v.Add(new("UNKNOWN_PROFILE_HAS_IDENTITY","An active UNKNOWN profile has an active identity.","Profile",r.GetString(0)));
    }

    private static async Task CheckNonterminalStagingCoherenceAsync(SqliteConnection c,List<CriticalIntegrityViolation> v,CancellationToken t)
    {
        await using var cmd=c.CreateCommand(); cmd.CommandText="""
            SELECT ii.import_item_id FROM import_items ii LEFT JOIN import_units iu ON ii.import_unit_id=iu.import_unit_id WHERE iu.import_unit_id IS NULL
            UNION ALL SELECT ii.import_item_id FROM import_items ii LEFT JOIN media a ON ii.candidate_media_id=a.media_id WHERE ii.candidate_media_id IS NOT NULL AND a.media_id IS NULL;
            """;
        await using var r=await cmd.ExecuteReaderAsync(t).ConfigureAwait(false); while(await r.ReadAsync(t).ConfigureAwait(false)) v.Add(new("STAGING_ORPHANED_REFERENCE","An import item has an orphaned durable reference.","ImportItem",r.GetString(0)));
    }

    private static async Task CheckManagedPathAuthorityAsync(
        SqliteConnection c,
        List<CriticalIntegrityViolation> v,
        CancellationToken t)
    {
        await using (var profiles = c.CreateCommand())
        {
            profiles.CommandText =
                """
                SELECT profile_id, path_state
                FROM profiles
                WHERE path_state IN ('PENDING','NEEDS_ATTENTION');
                """;
            await using var reader = await profiles.ExecuteReaderAsync(t).ConfigureAwait(false);
            while (await reader.ReadAsync(t).ConfigureAwait(false))
            {
                v.Add(new CriticalIntegrityViolation(
                    "UNRESOLVED_PROFILE_PATH_AUTHORITY",
                    $"Profile managed-path authority remains {reader.GetString(1)} after recovery.",
                    "Profile",
                    reader.GetString(0)));
            }
        }

        await using (var media = c.CreateCommand())
        {
            media.CommandText =
                """
                SELECT media_id, path_state
                FROM media
                WHERE path_state IN ('PENDING','NEEDS_ATTENTION');
                """;
            await using var reader = await media.ExecuteReaderAsync(t).ConfigureAwait(false);
            while (await reader.ReadAsync(t).ConfigureAwait(false))
            {
                v.Add(new CriticalIntegrityViolation(
                    "UNRESOLVED_MEDIA_PATH_AUTHORITY",
                    $"Media managed-path authority remains {reader.GetString(1)} after recovery.",
                    "Media",
                    reader.GetString(0)));
            }
        }
    }

    private static async Task CheckActiveAppearanceReferencesAsync(SqliteConnection c,List<CriticalIntegrityViolation> v,CancellationToken t)
    {
        await CheckPublishedProfileReadinessAsync(c, v, t).ConfigureAwait(false);
        await CheckActiveCoverReferencesAsync(c, v, t).ConfigureAwait(false);

        await using var cmd=c.CreateCommand(); cmd.CommandText="""
            SELECT pa.profile_id
            FROM profile_appearance pa
            JOIN profiles p ON p.profile_id = pa.profile_id
            LEFT JOIN media_assets ma ON ma.media_asset_id = pa.banner_media_asset_id
            LEFT JOIN media m ON m.media_id = ma.media_id
            WHERE p.trashed_at_ms IS NULL
              AND pa.banner_media_asset_id IS NOT NULL
              AND (ma.media_asset_id IS NULL
                   OR ma.role <> 'HOVER'
                   OR ma.state <> 'READY'
                   OR m.media_id IS NULL
                   OR m.state <> 'ACTIVE'
                   OR m.trashed_at_ms IS NOT NULL
                   OR m.media_type <> 'VIDEO')
            UNION ALL
            SELECT p.profile_id
            FROM profiles p
            WHERE p.kind='UNKNOWN' AND p.trashed_at_ms IS NULL AND p.cover_media_id IS NOT NULL;
            """;
        await using var r=await cmd.ExecuteReaderAsync(t).ConfigureAwait(false);
        while(await r.ReadAsync(t).ConfigureAwait(false))
            v.Add(new("INVALID_ACTIVE_APPEARANCE_REFERENCE","An active Profile has an invalid appearance reference.","Profile",r.GetString(0)));
    }

    private static async Task CheckPublishedProfileReadinessAsync(
        SqliteConnection c,
        List<CriticalIntegrityViolation> v,
        CancellationToken t)
    {
        await using var cmd = c.CreateCommand();
        cmd.CommandText = """
            SELECT p.profile_id
            FROM profiles p
            LEFT JOIN profile_appearance pa ON pa.profile_id = p.profile_id
            LEFT JOIN media_assets cover_asset ON cover_asset.media_asset_id = pa.cover_media_asset_id
            LEFT JOIN media cover_media ON cover_media.media_id = cover_asset.media_id
            WHERE p.kind = 'NORMAL'
              AND p.visibility = 'PUBLISHED'
              AND p.trashed_at_ms IS NULL
              AND (
                    pa.profile_id IS NULL
                 OR pa.schema_version <> 1
                 OR p.cover_media_id IS NULL
                 OR cover_asset.media_asset_id IS NULL
                 OR cover_asset.role <> 'THUMBNAIL'
                 OR cover_asset.state <> 'READY'
                 OR cover_media.media_id IS NULL
                 OR cover_media.media_id <> p.cover_media_id
                 OR cover_media.state <> 'ACTIVE'
                 OR cover_media.trashed_at_ms IS NOT NULL
                 OR NOT EXISTS (
                       SELECT 1
                       FROM profile_media relation
                       WHERE relation.profile_id = p.profile_id
                         AND relation.media_id = cover_media.media_id
                         AND relation.publication_import_unit_id IS NULL
                    )
              );
            """;
        await using var reader = await cmd.ExecuteReaderAsync(t).ConfigureAwait(false);
        while (await reader.ReadAsync(t).ConfigureAwait(false))
        {
            v.Add(new(
                "PUBLISHED_PROFILE_NOT_READY",
                "A published NORMAL Profile is missing its canonical ready appearance or Cover.",
                "Profile",
                reader.GetString(0)));
        }
    }

    private static async Task CheckActiveCoverReferencesAsync(
        SqliteConnection c,
        List<CriticalIntegrityViolation> v,
        CancellationToken t)
    {
        await using var cmd = c.CreateCommand();
        cmd.CommandText = """
            SELECT
                p.profile_id,
                a.media_id,
                a.state,
                a.trashed_at_ms,
                a.media_type,
                pa.overrides_json
            FROM profiles p
            LEFT JOIN media a ON p.cover_media_id = a.media_id
            LEFT JOIN profile_appearance pa ON p.profile_id = pa.profile_id
            WHERE p.trashed_at_ms IS NULL
              AND p.cover_media_id IS NOT NULL;
            """;

        await using var reader = await cmd.ExecuteReaderAsync(t).ConfigureAwait(false);
        while (await reader.ReadAsync(t).ConfigureAwait(false))
        {
            var profileId = reader.GetString(0);
            var validAsset = !reader.IsDBNull(1)
                && string.Equals(reader.GetString(2), "ACTIVE", StringComparison.Ordinal)
                && reader.IsDBNull(3)
                && !reader.IsDBNull(4);
            if (!validAsset)
            {
                AddInvalidAppearanceViolation(v, profileId);
                continue;
            }

            MediaType mediaType;
            try
            {
                mediaType = DbEnum.ParseMediaType(reader.GetString(4));
            }
            catch (FormatException)
            {
                AddInvalidAppearanceViolation(v, profileId);
                continue;
            }

            ProfileAppearanceOverrides overrides;
            try
            {
                overrides = reader.IsDBNull(5)
                    ? ProfileAppearanceOverrides.Default
                    : ProfileAppearanceOverrides.Parse(reader.GetString(5));
            }
            catch (Exception exception) when (exception is FormatException or ArgumentException or System.Text.Json.JsonException)
            {
                AddInvalidAppearanceViolation(v, profileId);
                continue;
            }

            CoverVisualSourceKind sourceKind;
            if (string.IsNullOrWhiteSpace(overrides.CoverSourceKind))
            {
                // An image Cover is unambiguous without an explicit source kind. A video Cover is not:
                // its exact frame timestamp is durable authority and must never be inferred.
                if (mediaType != MediaType.Image)
                {
                    AddInvalidAppearanceViolation(v, profileId);
                    continue;
                }

                sourceKind = CoverVisualSourceKind.Image;
            }
            else if (!Enum.TryParse(overrides.CoverSourceKind, ignoreCase: false, out sourceKind))
            {
                AddInvalidAppearanceViolation(v, profileId);
                continue;
            }

            if (!ProfileAppearanceRules.IsCoverVisualSourceValid(
                    mediaType,
                    sourceKind,
                    overrides.CoverVideoTimestampMilliseconds))
            {
                AddInvalidAppearanceViolation(v, profileId);
            }
        }
    }

    private static void AddInvalidAppearanceViolation(List<CriticalIntegrityViolation> violations, string profileId) =>
        violations.Add(new(
            "INVALID_ACTIVE_APPEARANCE_REFERENCE",
            "An active Profile has an invalid appearance reference.",
            "Profile",
            profileId));
}

public sealed record CriticalIntegrityViolation(string Code,string Message,string? TargetEntity=null,string? TargetId=null);
public sealed record CriticalIntegrityResult(bool Passed,IReadOnlyList<CriticalIntegrityViolation> Violations)
{
    public static CriticalIntegrityResult Success()=>new(true,[]);
    public static CriticalIntegrityResult Failure(IReadOnlyList<CriticalIntegrityViolation> v)=>new(false,v);
}
public sealed class CriticalIntegrityException : Exception
{
    public CriticalIntegrityResult Result { get; }
    public CriticalIntegrityException(CriticalIntegrityResult result):base("Critical integrity gate failed.") { Result=result??throw new ArgumentNullException(nameof(result)); }
}
