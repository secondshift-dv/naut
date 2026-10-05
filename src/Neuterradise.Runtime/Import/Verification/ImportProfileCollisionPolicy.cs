namespace Neuterradise.App.Import.Verification;

/// <summary>
/// Ownership collision gates intentionally require stronger identity evidence than an ordinary
/// face suggestion. Ambiguous/group evidence never changes or blocks ownership automatically.
/// </summary>
public static class ImportProfileCollisionPolicy
{
    public const double StrongSimilarityThreshold = 0.62;
}
