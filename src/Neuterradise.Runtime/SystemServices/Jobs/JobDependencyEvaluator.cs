using Neuterradise.App.SystemServices.Database.Reads;

namespace Neuterradise.App.SystemServices.Jobs;

public enum DependencyStatus
{
    Satisfied,
    Blocked,
    Cycle,
}

public sealed record DependencyEvaluation(DependencyStatus Status, IReadOnlyList<Guid> BlockedBy);

public sealed class JobDependencyEvaluator
{
    private readonly SchedulerReads _reads;

    public JobDependencyEvaluator(SchedulerReads reads)
    {
        _reads = reads ?? throw new ArgumentNullException(nameof(reads));
    }

    public async Task<DependencyEvaluation> EvaluateAsync(
        Guid jobId,
        CancellationToken cancellationToken = default)
    {
        var dependencies = await _reads.GetDependenciesAsync(jobId, cancellationToken).ConfigureAwait(false);

        var blockedBy = new List<Guid>();
        foreach (var dependency in dependencies)
        {
            if (dependency.JobId == dependency.DependsOnJobId)
            {
                return new DependencyEvaluation(DependencyStatus.Cycle, [dependency.DependsOnJobId]);
            }

            if (dependency.PredecessorState != JobState.Succeeded)
            {
                blockedBy.Add(dependency.DependsOnJobId);
            }
        }

        return blockedBy.Count == 0
            ? new DependencyEvaluation(DependencyStatus.Satisfied, [])
            : new DependencyEvaluation(DependencyStatus.Blocked, blockedBy);
    }

    public async Task<IReadOnlyList<Guid>> GetDirectDependentsAsync(
        Guid jobId,
        CancellationToken cancellationToken = default) =>
        await _reads.GetDirectDependentsAsync(jobId, cancellationToken).ConfigureAwait(false);
}
