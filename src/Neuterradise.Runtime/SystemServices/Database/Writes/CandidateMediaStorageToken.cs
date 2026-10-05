using Neuterradise.App.SystemServices.Storage;

namespace Neuterradise.App.SystemServices.Database.Writes;

internal static class CandidateStorageToken
{
    public static async Task<string> AllocateAsync(
        CatalogTransaction transaction,
        Guid assetId,
        CancellationToken cancellationToken)
    {
        var allocator = new StorageTokenAllocator();
        for (var attempt = 0; attempt < 29; attempt++)
        {
            var token = allocator.GetMediaCandidate(assetId, attempt).Value;
            await using var command = transaction.CreateCommand(
                "SELECT EXISTS(SELECT 1 FROM media WHERE media_storage_token = $token);");
            command.Parameters.AddWithValue("$token", token);
            var assigned = Convert.ToInt64(
                await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false),
                System.Globalization.CultureInfo.InvariantCulture) != 0;
            if (!assigned)
            {
                return token;
            }
        }

        throw new CatalogInvariantException($"No unique Media storage token candidate remained for {assetId:D}.");
    }
}
