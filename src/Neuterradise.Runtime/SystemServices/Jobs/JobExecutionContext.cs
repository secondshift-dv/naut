using Neuterradise.App.SystemServices.TimeAndIds;

namespace Neuterradise.App.SystemServices.Jobs;

public interface IJobProgressReporter
{
    Task ReportProgressAsync(long completed, long total, string? stage, CancellationToken cancellationToken = default);
}

public interface IJobCheckpointStore
{
    Task UpdateCheckpointAsync(string checkpointJson, CancellationToken cancellationToken = default);
}

public sealed class JobExecutionContext
{
    public JobExecutionContext(
        JobRecord job,
        CancellationToken cancellationToken,
        IJobProgressReporter progress,
        IJobCheckpointStore checkpoints,
        IClock clock)
    {
        ArgumentNullException.ThrowIfNull(job);
        ArgumentNullException.ThrowIfNull(progress);
        ArgumentNullException.ThrowIfNull(checkpoints);
        ArgumentNullException.ThrowIfNull(clock);

        JobId = job.JobId;
        Kind = job.Kind;
        Lane = job.Lane;
        OwnerType = job.OwnerType;
        OwnerId = job.OwnerId;
        Attempt = job.Attempt;
        CheckpointJson = job.CheckpointJson;
        CancellationToken = cancellationToken;
        Progress = progress;
        Checkpoints = checkpoints;
        Clock = clock;
    }

    public Guid JobId { get; }

    public string Kind { get; }

    public JobLane Lane { get; }

    public string OwnerType { get; }

    public Guid OwnerId { get; }

    public int Attempt { get; }

    public string? CheckpointJson { get; }

    public CancellationToken CancellationToken { get; }

    public IJobProgressReporter Progress { get; }

    public IJobCheckpointStore Checkpoints { get; }

    public IClock Clock { get; }
}
