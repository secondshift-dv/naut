using Neuterradise.App.SystemServices.Database;

namespace Neuterradise.App.Media.Model;

public static class ModelRenderEligibility
{
    public static bool IsEligible(string? fileName, string dependencyStatus) =>
        Path.GetExtension(fileName)?.ToLowerInvariant() switch
        {
            ".glb" => dependencyStatus == "SELF_CONTAINED",
            ".obj" or ".stl" or ".3mf" => dependencyStatus is "SELF_CONTAINED" or "COMPLETE",
            ".fbx" => dependencyStatus is "SELF_CONTAINED" or "COMPLETE" or "DEPENDENCIES_UNKNOWN",
            _ => false,
        };

    public static async Task<bool> IsEligibleAsync(CatalogDb catalog, Guid mediaId, CancellationToken ct = default)
    {
        await using var connection = await catalog.OpenConnectionAsync(ct).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT coalesce(current_managed_file_name, original_file_name), dependency_status
            FROM media WHERE media_id = $id AND media_type = 'MODEL';
            """;
        command.Parameters.AddWithValue("$id", DbGuid.Format(mediaId));
        await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        return await reader.ReadAsync(ct).ConfigureAwait(false) && !reader.IsDBNull(0)
            && IsEligible(reader.GetString(0), reader.GetString(1));
    }
}
