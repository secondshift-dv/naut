namespace Neuterradise.App.Import.Verification;

public sealed record VerificationBlocker(
    Guid? ImportItemId,
    string Code,
    string Message,
    string? Details = null);
