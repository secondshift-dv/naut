using Neuterradise.App.SystemServices.Storage;

namespace Neuterradise.App.SystemServices.Jobs.Handlers;

public interface IProfileRenameReconciliationJobOperation
{
    Task<StorageOperationResult> ExecuteAsync(Guid profileId, CancellationToken cancellationToken);
}

public sealed class ProfileRenameReconciliationJobHandler : AuthorizedJobHandler
{

    public ProfileRenameReconciliationJobHandler(PathReconciler reconciler)
        : this(new PathReconcilerOperation(reconciler))
    {
    }

    public ProfileRenameReconciliationJobHandler(IProfileRenameReconciliationJobOperation operation)
        : base(
            "ProfileRenameReconciliation",
            [JobLane.Io],
            "Profile",
            new StorageOperationAdapter(operation),
            runOffCallingThread: false)
    {
    }

    private sealed class PathReconcilerOperation : IProfileRenameReconciliationJobOperation
    {
        private readonly PathReconciler _reconciler;

        public PathReconcilerOperation(PathReconciler reconciler) =>
            _reconciler = reconciler ?? throw new ArgumentNullException(nameof(reconciler));

        public Task<StorageOperationResult> ExecuteAsync(
            Guid profileId,
            CancellationToken cancellationToken) =>
            _reconciler.ReconcileProfileRenameAsync(profileId, cancellationToken);
    }

    private sealed class StorageOperationAdapter : IAuthorizedJobOperation
    {
        private readonly IProfileRenameReconciliationJobOperation _operation;

        public StorageOperationAdapter(IProfileRenameReconciliationJobOperation operation) =>
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
