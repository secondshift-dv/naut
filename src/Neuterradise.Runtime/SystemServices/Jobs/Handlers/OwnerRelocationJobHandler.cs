using Neuterradise.App.SystemServices.Storage;

namespace Neuterradise.App.SystemServices.Jobs.Handlers;

public interface IOwnerRelocationJobOperation
{
    Task<StorageOperationResult> ExecuteAsync(Guid assetId, CancellationToken cancellationToken);
}

public sealed class OwnerRelocationJobHandler : AuthorizedJobHandler
{
    public OwnerRelocationJobHandler(PathReconciler reconciler)
        : this(new PathReconcilerOperation(reconciler))
    {
    }

    public OwnerRelocationJobHandler(IOwnerRelocationJobOperation operation)
        : base("OwnerRelocation", [JobLane.Io], "Media", new StorageOperationAdapter(operation), runOffCallingThread: false)
    {
    }

    private sealed class PathReconcilerOperation : IOwnerRelocationJobOperation
    {
        private readonly PathReconciler _reconciler;

        public PathReconcilerOperation(PathReconciler reconciler) =>
            _reconciler = reconciler ?? throw new ArgumentNullException(nameof(reconciler));

        public Task<StorageOperationResult> ExecuteAsync(
            Guid assetId,
            CancellationToken cancellationToken) =>
            _reconciler.ReconcileOwnerRelocationAsync(assetId, cancellationToken);
    }

    private sealed class StorageOperationAdapter : IAuthorizedJobOperation
    {
        private readonly IOwnerRelocationJobOperation _operation;

        public StorageOperationAdapter(IOwnerRelocationJobOperation operation) =>
            _operation = operation ?? throw new ArgumentNullException(nameof(operation));

        public async Task<JobExecutionResult> ExecuteAsync(
            JobExecutionContext context,
            CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(context);

            var result = await _operation.ExecuteAsync(context.OwnerId, cancellationToken)
                .ConfigureAwait(false);
            return StorageJobResultMapper.Map(result);
        }
    }
}
