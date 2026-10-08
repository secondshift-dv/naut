namespace Neuterradise.App.SystemServices.Updates;

public enum UpdatePhase { Checking, Planning, Downloading, Verifying, Preparing, Restarting }

public sealed record UpdateProgress(
    UpdatePhase Phase, long CompletedBytes, long? TotalBytes, int CompletedFiles, int TotalFiles)
{
    public double? Percentage => TotalBytes switch
    {
        null => null,
        0 => 100,
        > 0 => 100d * CompletedBytes / TotalBytes.Value,
        _ => null
    };
}

internal sealed class UpdateProgressCallback(Action<UpdateProgress> callback) : IProgress<UpdateProgress>
{
    public void Report(UpdateProgress value) => callback(value);
}
