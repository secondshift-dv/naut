using System.Diagnostics;
using System.Reflection;
using Neuterradise.App.Settings;
using Neuterradise.App.SystemServices;
using Neuterradise.App.SystemServices.Database;
using Neuterradise.App.SystemServices.Storage;
using Neuterradise.App.SystemServices.Updates;

static class UpdateResponsivenessProbe
{
    public static void Run(string root)
    {
        static void Require(bool condition, string error)
        {
            if (!condition) throw new InvalidOperationException(error);
        }

        var state = new AppStatePaths(Path.Combine(root, "progress-state"));
        var install = new InstallPaths(Path.Combine(root, "progress-install"));
        var vault = Path.Combine(root, "progress-vault");
        using var http = new HttpClient();
        var download = new UpdateDownloadService(http, state);
        var validator = new UpdatePackageValidator(install);
        using var coordinator = new UpdateCoordinator(new CatalogMutationAdmissionGate(),
            new AppConfigurationStore(state), state, install, vault,
            new UpdateCheckService(http), download, new LocalUpdatePackageReader(),
            new UpdateTrustPolicy(), new UpdatePackageStager(state, validator),
            new UpdateHandoffService(state, install), _ => { });
        var publish = typeof(UpdateCoordinator).GetMethod("SetState", BindingFlags.Instance | BindingFlags.NonPublic)!
            .CreateDelegate<Action<UpdatePresentationState>>(coordinator);
        var report = typeof(UpdateCoordinator).GetMethod("ReportProgress", BindingFlags.Instance | BindingFlags.NonPublic)!
            .CreateDelegate<Action<UpdateProgress, string?>>(coordinator);
        using var vm = new SettingsViewModel(updateCoordinator: coordinator);
        var queue = new QueuedUiContext();
        UiDispatch.Install(queue);
        var changes = new List<string?>();
        vm.PropertyChanged += (_, args) => changes.Add(args.PropertyName);
        var progress = new UpdateProgress(UpdatePhase.Downloading, 1, 20001, 0, 1);
        var downloading = new UpdatePresentationState(UpdateCoordinator.StatusDownloading, null, true,
            progress.Percentage, false, "0.0.5", progress);

        Task.Run(() =>
        {
            for (var i = 1; i <= 20000; i++)
                publish(downloading with { Transfer = progress with { CompletedBytes = i } });
        }).GetAwaiter().GetResult();
        Require(queue.Pending == 1, "Download burst accumulated UI callbacks.");
        queue.Drain();
        Require(vm.UpdateStatusText.Contains("100%"),
            "Latest byte progress was not presented.");
        Require(!vm.CanManageUpdates && !vm.CheckForUpdatesCommand.CanExecute(null),
            "A second update action remained available while downloading.");
        Require(changes.Count <= 5, "One progress snapshot emitted redundant property notifications.");

        changes.Clear();
        publish(coordinator.State);
        queue.Drain();
        Require(changes.Count == 0, "Identical update state emitted property changes.");

        var shutdownRan = false;
        Task.Run(() =>
        {
            for (var i = 0; i < 20000; i++) publish(downloading);
            publish(new(UpdateCoordinator.StatusRestarting, null, false, null, true, "0.0.5"));
            UiDispatch.Post(() => shutdownRan = true);
        }).GetAwaiter().GetResult();
        Require(queue.Pending == 2, "Update progress starved the queued shutdown request.");
        queue.Drain();
        Require(shutdownRan && vm.UpdateStatusText.Contains("closing") && !vm.CanManageUpdates,
            "Restart state or shutdown delivery was lost.");

        var publishedCount = 0;
        coordinator.StateChanged += _ => publishedCount++;
        var watch = Stopwatch.StartNew();
        for (var i = 1; i <= 20000; i++) report(progress with { CompletedBytes = i }, "0.0.5");
        var elapsed = watch.Elapsed;
        report(progress with { CompletedBytes = 20001, CompletedFiles = 1 }, "0.0.5");
        Require(publishedCount <= elapsed.TotalMilliseconds / 100 + 3,
            "Intermediate progress publication was not rate bounded.");
        Require(coordinator.State.Transfer?.CompletedBytes == 20001, "Final download progress was throttled away.");
        report(progress with { Phase = UpdatePhase.Verifying }, "0.0.5");
        Require(coordinator.State.Status == UpdateCoordinator.StatusStaging, "Phase transition was throttled away.");

        vm.Dispose();
        var countBeforeDisposedDrain = changes.Count;
        queue.Drain();
        Require(changes.Count == countBeforeDisposedDrain, "Disposed Settings received queued progress.");
        UiDispatch.Install(new SynchronizationContext());
        Console.WriteLine("UPDATE_PROGRESS_QUEUE_RATE_FINAL_STATE_COMMANDS_SHUTDOWN=PASS");
    }

    private sealed class QueuedUiContext : SynchronizationContext
    {
        private readonly Queue<(SendOrPostCallback Callback, object? State)> _queue = new();
        public int Pending { get { lock (_queue) return _queue.Count; } }
        public override void Post(SendOrPostCallback callback, object? state)
        {
            lock (_queue) _queue.Enqueue((callback, state));
        }
        public void Drain()
        {
            while (true)
            {
                (SendOrPostCallback Callback, object? State) item;
                lock (_queue)
                {
                    if (_queue.Count == 0) return;
                    item = _queue.Dequeue();
                }
                item.Callback(item.State);
            }
        }
    }
}
