using System.Net;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Neuterradise.App.Import.Intake;
using Neuterradise.App.Media;
using Neuterradise.App.SystemServices.Storage;
using Neuterradise.App.SystemServices.Database;
using Neuterradise.App.SystemServices.Updates;
using Neuterradise.Release.Contracts;
using Neuterradise.Updater;

static void Require(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
static string Hash(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));
var root = Path.GetFullPath(args[0]);
Directory.CreateDirectory(root);
if (args.Length > 1 && args[1] == "--replace-package")
{
    var baselineZip = args[2];
    var distribution = args[3];
    var manifestBytes = await File.ReadAllBytesAsync(Path.Combine(distribution, "update.json"));
    var signed = args.Contains("--require-signature", StringComparer.Ordinal);
    if (signed)
    {
        var signature = await File.ReadAllBytesAsync(Path.Combine(distribution, "update-signature.json"));
        Require(new UpdatePublisherSignatureVerifier().Verify(manifestBytes, signature).Verified,
            "Published update signature failed verification.");
    }
    var target = UpdateManifest.Parse(Encoding.UTF8.GetString(manifestBytes));
    var zip = Path.Combine(distribution, $"naut-v{target.ProductVersion}-{target.RuntimeIdentifier}.zip");
    await using var zipStream = File.OpenRead(zip);
    var zipHash = Convert.ToHexStringLower(await SHA256.HashDataAsync(zipStream));
    Require(zipStream.Length == target.PayloadByteLength && zipHash == target.PayloadSha256, "ZIP differs from its update manifest.");

    var installation = new InstallPaths(Path.Combine(root, "baseline"));
    ZipFile.ExtractToDirectory(baselineZip, installation.Root);
    var before = ReadInstalledVersion(installation.Root);
    Require(Version.Parse(before) < Version.Parse(target.ProductVersion), "Baseline is not older than the candidate.");

    var vault = new VaultPaths(Path.Combine(root, "vault"));
    vault.EnsureStructuralDirectories();
    var catalog = new CatalogDb(vault);
    await catalog.InitializeAsync();
    await catalog.ProfileWrites.CreateNormalProfileAsync(Guid.NewGuid(), Guid.NewGuid(), "Updater fixture");
    await File.WriteAllBytesAsync(Path.Combine(vault.ProfilesPath, "synthetic-media.bin"), [13, 21, 34]);
    Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
    var vaultBefore = ReadVaultInventory(vault.Root);

    var appState = new AppStatePaths(Path.Combine(root, "state"));
    var operation = Guid.NewGuid();
    var stager = new UpdatePackageStager(appState, new UpdatePackageValidator(installation));
    var staging = await stager.ExtractAndValidateAsync(zip, operation, target);
    Require(staging.IsAccepted, "Package staging failed: " + staging.SafeError);
    var toolsRoot = appState.GetUpdateToolsPath(operation);
    Directory.CreateDirectory(toolsRoot);
    var handoffFile = Path.Combine(toolsRoot, "handoff.json");
    var officialHandoff = new UpdateHandoff(operation, Path.Combine(appState.UpdateStagingPath, operation.ToString("D")),
        installation.Root, target, vault.Root, DateTimeOffset.UtcNow.AddMinutes(5));
    var encoded = JsonSerializer.SerializeToUtf8Bytes(officialHandoff, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase });
    await File.WriteAllBytesAsync(handoffFile, encoded);
    await new UpdateStateStore(appState).PublishHandoffPendingAsync(operation);
    var replacement = await ReplacementEngine.ExecuteAsync(handoffFile, Hash(encoded), target.ComputeAuthoritySha256(), null, launchApp: false);
    Require(replacement.Success && ReadInstalledVersion(installation.Root) == target.ProductVersion,
        "Headless replacement did not install the target version: " + replacement);
    Require(vaultBefore.SequenceEqual(ReadVaultInventory(vault.Root)), "Headless replacement changed Vault data.");
    Console.WriteLine($"PACKAGED_UPGRADE_{before}_TO_{target.ProductVersion}=PASS;ZIP_SHA256={zipHash};SIGNED={signed}");
    return;
}
if (args.Length > 1)
{
    var context = new System.Runtime.Loader.AssemblyLoadContext("packaged", true);
    var package = Path.GetFullPath(args[1]);
    var runtime = context.LoadFromAssemblyPath(Path.Combine(package, "runtime", "Neuterradise.Runtime.dll"));
    var productIdentity = runtime.GetType("Neuterradise.App.SystemServices.ProductIdentity")!;
    var release = UpdateReleaseManifest.Parse(File.ReadAllText(Path.Combine(package, "release-manifest.json")));
    Require((string?)productIdentity.GetProperty("Version")!.GetValue(null) == release.ProductVersion
        && (string?)productIdentity.GetProperty("RuntimeId")!.GetValue(null) == release.RuntimeIdentifier,
        "Packaged product version or runtime identifier differs from the release manifest");
    Console.WriteLine("PACKAGED_UPDATE_IDENTITY=PASS");
    var classifier = runtime.GetType("Neuterradise.App.Import.Intake.SupportedMediaClassifier")!;
    foreach (var extension in new[] { ".mpg", ".mpeg", ".MPG", ".MPEG" })
        Require(classifier.GetMethod("Classify")!.Invoke(null, new object[] { extension })?.ToString() == "Video", "Packaged MPEG regression");
    Console.WriteLine("PACKAGED_MPG_MPEG=PASS");
    return;
}
UpdateResponsivenessProbe.Run(root);
foreach (var extension in new[] { ".mpg", ".mpeg", ".MPG", ".MPEG" })
    Require(SupportedMediaClassifier.Classify(extension) == MediaType.Video && SupportedMediaClassifier.BuildFileDialogFilter().Contains("*" + extension.ToLowerInvariant()), "MPEG classifier or picker regression");
var installRoot = Path.Combine(root, "install");
var vaultRoot = Path.Combine(root, "protected-vault");
Directory.CreateDirectory(vaultRoot);
File.WriteAllText(Path.Combine(vaultRoot, "sentinel"), "protected");
var state = new AppStatePaths(Path.Combine(root, "state"));
var files = new List<UpdateManifestFile>();
var data = new Dictionary<string, byte[]>();
var members = ReleaseContract.Current.RequiredMembers.Append("runtime/OpenCvSharpExtern.dll")
    .Append(IncrementalUpdateService.MarkerPath).Append("runtime/new.dll")
    .Append("runtime/duplicate.dll").Append("runtime/empty.marker").ToArray();
foreach (var member in members)
{
    var bytes = member == IncrementalUpdateService.MarkerPath ? Encoding.UTF8.GetBytes("{\"schemaVersion\":1}")
        : member == "runtime/empty.marker" ? Array.Empty<byte>()
        : Encoding.UTF8.GetBytes("target:" + (member == "runtime/duplicate.dll" ? "runtime/new.dll" : member));
    files.Add(new(member, bytes.Length, Hash(bytes), "runtime"));
    data[Hash(bytes)] = bytes;
    if (member is "runtime/new.dll" or "runtime/duplicate.dll" or "runtime/empty.marker") continue;
    var path = Path.Combine(installRoot, member);
    Directory.CreateDirectory(Path.GetDirectoryName(path)!);
    File.WriteAllBytes(path, bytes);
}
var changed = files.Single(f => f.RelativePath == "naut.exe");
var corrupt = files.Single(f => f.RelativePath == "runtime/naut.exe");
File.WriteAllText(Path.Combine(installRoot, changed.RelativePath), "changed length");
File.WriteAllBytes(Path.Combine(installRoot, corrupt.RelativePath), new byte[corrupt.ByteLength]);
File.WriteAllText(Path.Combine(installRoot, "runtime/obsolete.dll"), "obsolete");
var manifest = new UpdateManifest(1, "neuterradise", "0.0.4", "win-x64", 300_000_000, new string('0',64), "0.0.1", files);
var feed = new Uri("https://github.com/secondshift-dv/naut/releases/download/v0.0.4/update.json");
var plan = await IncrementalUpdateService.PlanAsync(manifest, installRoot);
Require(plan.DownloadFiles.Count == 5 && plan.DownloadFiles.Contains(changed) && plan.DownloadFiles.Contains(corrupt)
    && plan.DownloadFiles.Any(f => f.RelativePath == "runtime/new.dll"), "Planner reused corrupt/changed bytes or missed new file");
Require(plan.ReusedFiles.Count == files.Count - 5, "Unchanged file scheduled for transfer");
Require(plan.DownloadBytes == changed.ByteLength + corrupt.ByteLength
    + files.Single(f => f.RelativePath == "runtime/new.dll").ByteLength, "Duplicate content counted twice");
foreach (var unsafePath in new[] { "../escape", "runtime//a", "runtime/./a", "runtime/a.", "runtime/CON.bin", "runtime/a:stream" })
{
    try { await IncrementalUpdateService.PlanAsync(manifest with { Files = new[] { changed with { RelativePath = unsafePath } } }, installRoot); throw new Exception("Unsafe path accepted"); }
    catch (FormatException) { }
}
try { (manifest with { Files = new[] { changed, changed with { RelativePath = "NAUT.exe" } } }).Validate(); throw new Exception("Duplicate accepted"); }
catch (FormatException) { }
var handler = new FixtureHandler(request =>
{
    var name = Path.GetFileName(request.RequestUri!.AbsolutePath);
    if (name == IncrementalUpdateService.MetadataName) return data[files.Single(f=>f.RelativePath==IncrementalUpdateService.MarkerPath).Sha256];
    return data[name["update-file-".Length..^4]];
});
var install = new InstallPaths(installRoot);
install.EnsureDisjointFrom(state, vaultRoot);
foreach (var overlapping in new[] { installRoot, state.Root, Path.Combine(installRoot, "vault") })
{
    try { install.EnsureDisjointFrom(state, overlapping); throw new Exception("Overlapping root accepted"); }
    catch (ArgumentException) { }
}
using var http = new HttpClient(handler);
var download = new UpdateDownloadService(http, state);
var service = new IncrementalUpdateService(state, install, download, new UpdatePackageValidator(install));
var progress = new Recorder();
var op = Guid.NewGuid();
var staged = await service.StageAsync(feed, manifest, op, progress);
Require(staged.IsSuccess, staged.SafeError ?? "Staging failed");
var payload = state.ResolveContainedPath(AppStatePathArea.UpdateStaging, op.ToString("D"));
Require(!File.Exists(Path.Combine(payload, "runtime/obsolete.dll")), "Obsolete member copied");
Require(handler.Requests.Count == 4 && handler.Requests.All(uri=>!uri.AbsolutePath.Contains(UpdateManifestFile.EmptySha256)), "Unchanged/empty members downloaded");
try { (changed with { ByteLength=0 }).Validate(); throw new Exception("Impossible empty identity accepted"); }
catch (FormatException) { }
var points = progress.Values.Where(v => v.Phase == UpdatePhase.Downloading).ToArray();
Require(points.Length > 1 && points.All(v => v.TotalBytes == plan.DownloadBytes && v.CompletedBytes <= v.TotalBytes
    && v.Percentage is >= 0 and <= 100), "Incremental denominator/bounds incorrect");
Require(points.Zip(points.Skip(1)).All(pair => pair.First.CompletedBytes <= pair.Second.CompletedBytes), "Byte progress regressed");
Require(points[^1].CompletedBytes == plan.DownloadBytes && points[^1].CompletedFiles == 5, "Final progress incomplete");
Require(new UpdateProgress(UpdatePhase.Planning, 0, null, 0, 0).Percentage is null, "Unknown denominator is determinate");
foreach (var file in files)
    File.Copy(Path.Combine(payload, file.RelativePath), Path.Combine(installRoot, file.RelativePath), true);
var zero = await IncrementalUpdateService.PlanAsync(manifest, installRoot);
Require(zero.DownloadBytes == 0 && zero.DownloadFiles.Count == 0, "Zero plan incorrect");
var zeroProgress = new Recorder();
var zeroStage = await service.StageAsync(feed, manifest, Guid.NewGuid(), zeroProgress);
Require(zeroStage.IsSuccess && zeroProgress.Values.Any(v => v.Phase == UpdatePhase.Downloading && v.Percentage == 100 && v.TotalBytes == 0), "Zero-download staging failed");
var absent = await service.StageAsync(feed, manifest with { Files = files.Where(f=>f.RelativePath!=IncrementalUpdateService.MarkerPath).ToArray() }, Guid.NewGuid());
Require(absent.CanFallback && !absent.IsSuccess, "Missing protocol did not allow ZIP");
foreach (var protocol in new[] { "{\"schemaVersion\":2}", "{}" })
{
    var bytes = Encoding.UTF8.GetBytes(protocol);
    var marker = files.Single(f=>f.RelativePath==IncrementalUpdateService.MarkerPath) with { Sha256=Hash(bytes), ByteLength=bytes.Length };
    using var protocolHttp = new HttpClient(new FixtureHandler(_=>bytes));
    var protocolService = new IncrementalUpdateService(state, install, new UpdateDownloadService(protocolHttp,state), new UpdatePackageValidator(install));
    var result = await protocolService.StageAsync(feed, manifest with { Files=files.Where(f=>f.RelativePath!=marker.RelativePath).Append(marker).ToArray() }, Guid.NewGuid());
    Require(!result.IsSuccess && result.CanFallback == protocol.Contains('2'), "Unsupported/malformed signed protocol classification");
}
foreach (var fault in new[] { "missing", "network", "corrupt" })
{
    var marker = files.Single(f=>f.RelativePath==IncrementalUpdateService.MarkerPath);
    using var assetHttp = new HttpClient(new FixtureHandler(request =>
    {
        if (request.RequestUri!.AbsolutePath.EndsWith(IncrementalUpdateService.MetadataName)) return data[marker.Sha256];
        throw new HttpRequestException("missing asset");
    }, fault == "corrupt" ? "corrupt" : null));
    File.WriteAllText(Path.Combine(installRoot,"naut.exe"), "changed");
    var assetService = new IncrementalUpdateService(state, install, new UpdateDownloadService(assetHttp,state),new UpdatePackageValidator(install));
    var result = await assetService.StageAsync(feed, manifest, Guid.NewGuid());
    Require(!result.IsSuccess && result.CanFallback == (fault != "corrupt"), "Incremental transport/integrity classification");
    File.Copy(Path.Combine(payload,"naut.exe"), Path.Combine(installRoot,"naut.exe"),true);
}
foreach (var fault in new[] { "missing", "network", "redirect", "corrupt", "partial", "length" })
{
    using var faultHttp = new HttpClient(new FixtureHandler(_ => Encoding.UTF8.GetBytes("abc"), fault));
    var faultDownload = new UpdateDownloadService(faultHttp, state);
    var id = Guid.NewGuid();
    var result = await faultDownload.DownloadAsync(new Uri(feed, "test.bin"), id, 3, Hash(Encoding.UTF8.GetBytes("abc")));
    Require(!result.IsSuccess && result.CanFallback == (fault is "missing" or "network"), "Wrong fallback classification: " + fault);
    Require(!File.Exists(Path.Combine(state.GetUpdateToolsPath(id), "payload.partial")), "Partial download survived failure");
}
using (var cts = new CancellationTokenSource())
using (var cancelHttp = new HttpClient(new FixtureHandler(_ => new byte[2_000_000])))
{
    var id = Guid.NewGuid();
    var bytes = new byte[2_000_000];
    var cancellationProgress = new Canceller(cts);
    var result = await new UpdateDownloadService(cancelHttp, state).DownloadAsync(new Uri(feed,"cancel.bin"), id, bytes.Length, Hash(bytes), cts.Token, cancellationProgress);
    Require(!result.IsSuccess && !result.CanFallback && !File.Exists(Path.Combine(state.GetUpdateToolsPath(id), "payload.partial")), "Cancellation completion/cleanup failed");
}
using (var badSignatureHttp = new HttpClient(new FixtureHandler(_ => Encoding.UTF8.GetBytes("{}"))))
{
    var result = await new UpdateCheckService(badSignatureHttp).CheckAsync(feed, new(feed.AbsoluteUri, false));
    Require(!result.IsAvailable, "Invalid publisher signature accepted");
}
foreach (var bad in new[] { manifest with { ProductId="wrong" }, manifest with { RuntimeIdentifier="linux-x64" } })
    Require(!new UpdateTrustPolicy().Evaluate(bad, new(feed.AbsoluteUri,false),false,false,true).Accepted, "Product/runtime mismatch accepted");

var installed = Version.Parse(Neuterradise.App.SystemServices.ProductIdentity.Version);
var nextVersion = new Version(installed.Major, installed.Minor, checked(installed.Build + 1)).ToString(3);
var bridge = manifest with { ProductVersion = nextVersion, MinimumCompatibleVersion = nextVersion };
var localZip = Path.Combine(root, "bridge.zip");
using (var archive = ZipFile.Open(localZip, ZipArchiveMode.Create))
{
    using var writer = new StreamWriter(archive.CreateEntry("release-manifest.json").Open());
    writer.Write(JsonSerializer.Serialize(new UpdateReleaseManifest(bridge.SchemaVersion, bridge.ProductId,
        bridge.ProductVersion, bridge.RuntimeIdentifier, bridge.Files, bridge.MinimumCompatibleVersion)));
}
var localBridge = await new LocalUpdatePackageReader().ReadAsync(localZip);
Require(localBridge.IsSuccess && localBridge.Manifest?.MinimumCompatibleVersion == bridge.MinimumCompatibleVersion,
    "Local inspection lost compatibility floor");
Require(!new UpdateTrustPolicy().Evaluate(localBridge.Manifest!, new(null, true), true, true).Accepted,
    "Local ZIP bypassed compatibility floor");
Require(new UpdateTrustPolicy().Evaluate(localBridge.Manifest! with { MinimumCompatibleVersion = installed.ToString(3) },
    new(null, true), true, true).Accepted, "Local ZIP rejected the exact compatibility floor");
Require(!new UpdateTrustPolicy().Evaluate(localBridge.Manifest!, new(feed.AbsoluteUri, false), false, false, true).Accepted,
    "Publisher-verified update bypassed compatibility floor");
Require(!new UpdatePackageValidator(install).Validate(manifest with { MinimumCompatibleVersion = null }, payload).IsAccepted,
    "Staged payload compatibility mismatch accepted");
using (var lease = await UpdateOperationLease.AcquireAsync(state.Root))
{
    using var cancel = new CancellationTokenSource(TimeSpan.FromMilliseconds(150));
    try { using var unexpected = await UpdateOperationLease.AcquireAsync(state.Root, cancel.Token); throw new Exception("Concurrent update lease accepted"); }
    catch (OperationCanceledException) { }
}
using (await UpdateOperationLease.AcquireAsync(state.Root)) { }
var alias = Path.Combine(root, "vault-alias");
using (var junction = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("cmd.exe",
    $"/d /c mklink /J \"{alias}\" \"{vaultRoot}\"") { UseShellExecute = false, CreateNoWindow = true }))
{
    await junction!.WaitForExitAsync();
    Require(junction.ExitCode == 0, "Disposable junction creation failed");
}
try { UpdatePathGuard.RequireDisjoint(vaultRoot, alias); throw new Exception("Physical alias accepted"); }
catch (IOException) { }
try { UpdatePathGuard.DeleteTree(alias); throw new Exception("Cleanup traversed root junction"); }
catch (IOException) { }
var cleanupRoot = Path.Combine(root, "cleanup-handle-probe");
Directory.CreateDirectory(cleanupRoot);
Directory.Move(alias, Path.Combine(cleanupRoot, "nested-alias"));
File.WriteAllText(Path.Combine(cleanupRoot, "disposable.txt"), "owned");
UpdatePathGuard.DeleteTree(cleanupRoot);
Require(!Directory.Exists(cleanupRoot) && File.ReadAllText(Path.Combine(vaultRoot, "sentinel")) == "protected", "Handle cleanup traversed junction or failed to delete disposable tree");
Console.WriteLine("UPDATE_HANDLE_CLEANUP_NO_REPARSE_TRAVERSAL=PASS");
Console.WriteLine("UPDATE_LOCAL_COMPATIBILITY_AND_ROOT_ALIAS_LEASE=PASS");
var atomicRoot = Path.Combine(root, "atomic-race");
Directory.CreateDirectory(atomicRoot);
var atomicPath = Path.Combine(atomicRoot, "journal.json");
var sentinelPath = Path.Combine(vaultRoot, "sentinel");
using (var link = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("cmd.exe",
    $"/d /c mklink /H \"{atomicPath}.tmp\" \"{sentinelPath}\"") { UseShellExecute = false, CreateNoWindow = true }))
{
    await link!.WaitForExitAsync();
    Require(link.ExitCode == 0, "Temporary hardlink creation failed");
}
await UpdatePathGuard.WriteAtomicAsync(atomicPath, Encoding.UTF8.GetBytes("new journal"));
Require(File.ReadAllText(sentinelPath) == "protected" && File.ReadAllText(atomicPath) == "new journal",
    "Atomic journal write followed a temporary hardlink");
using (UpdatePathGuard.PinDirectory(atomicRoot))
{
    try { Directory.Move(atomicRoot, atomicRoot + "-moved"); throw new Exception("Pinned ancestor moved"); }
    catch (IOException) { }
}
try { UpdatePathGuard.CopyNewFile(atomicPath, sentinelPath); throw new Exception("Copy overwrote existing target"); }
catch (IOException) { }
Require(File.ReadAllText(sentinelPath) == "protected", "Copy touched protected bytes");
Console.WriteLine("UPDATE_PINNED_ANCESTOR_ATOMIC_HARDLINK_CREATE_NEW=PASS");


var handoffRoot = state.GetUpdateToolsPath(op);
Directory.CreateDirectory(handoffRoot);
var handoffPath = Path.Combine(handoffRoot, "handoff.json");
async Task<ReplacementResult> Replace(UpdateHandoff handoff, string? authority = null)
{
    var bytes = JsonSerializer.SerializeToUtf8Bytes(handoff, new JsonSerializerOptions { PropertyNamingPolicy=JsonNamingPolicy.CamelCase });
    await File.WriteAllBytesAsync(handoffPath, bytes);
    return await ReplacementEngine.ExecuteAsync(handoffPath, Hash(bytes), authority ?? manifest.ComputeAuthoritySha256(), null, false);
}
var handoff = new UpdateHandoff(op, payload, installRoot, manifest, vaultRoot, DateTimeOffset.UtcNow.AddMinutes(1));
Require(!(await Replace(handoff)).Success, "Unpublished handoff was accepted");
await new UpdateStateStore(state).PublishHandoffPendingAsync(op);
Require(!(await Replace(handoff, new string('1',64))).Success, "Manifest authority mismatch accepted");
Require(!(await Replace(handoff with { VaultRoot=installRoot })).Success, "Vault overlap replacement accepted");
var deferredHandoff = handoff with { ShutdownDeadlineUtc = DateTimeOffset.UtcNow.AddMilliseconds(100) };
var deferredBytes = JsonSerializer.SerializeToUtf8Bytes(deferredHandoff, new JsonSerializerOptions { PropertyNamingPolicy=JsonNamingPolicy.CamelCase });
await File.WriteAllBytesAsync(handoffPath, deferredBytes);
var deferred = await ReplacementEngine.ExecuteAsync(handoffPath, Hash(deferredBytes),
    manifest.ComputeAuthoritySha256(), Environment.ProcessId, false);
Require(!deferred.Success && File.Exists(Path.Combine(installRoot, "runtime/obsolete.dll"))
    && !Directory.Exists(installRoot + ".backup." + op.ToString("N")),
    "Updater mutated InstallRoot before the live parent exited.");
Require(File.ReadAllText(Path.Combine(vaultRoot, "sentinel")) == "protected", "Deferred update touched Vault.");
Console.WriteLine("UPDATE_SHUTDOWN_DEADLINE_DEFERS_WITHOUT_MUTATION=PASS");
Require((await Replace(handoff with { ShutdownDeadlineUtc = DateTimeOffset.UtcNow.AddMinutes(1) })).Success, "Replacement failed");
var backup = installRoot + ".backup." + op.ToString("N");
var preserved = ReadVaultInventory(backup);
Require(!(await Replace(handoff)).Success && preserved.SequenceEqual(ReadVaultInventory(backup)),
    "Pending operation replay changed the original backup");
await new UpdateStateStore(state).PublishCatalogWriteStartedAsync(op, backup);
Require(!(await Replace(handoff)).Success && preserved.SequenceEqual(ReadVaultInventory(backup)),
    "Post-cutoff replay changed the original backup");
var resumed = await new UpdateStartupRecovery(state, install, vaultRoot).ReconcileAsync();
Require(resumed.CanContinue && resumed.ReplacementInstalled, "Valid post-cutoff replacement was not verified");
var launcher = Path.Combine(installRoot, "naut.exe");
var approvedLauncher = await File.ReadAllBytesAsync(launcher);
await File.WriteAllTextAsync(launcher, "tampered");
var tampered = await new UpdateStartupRecovery(state, install, vaultRoot).ReconcileAsync();
Require(!tampered.CanContinue && preserved.SequenceEqual(ReadVaultInventory(backup)),
    "Tampered post-cutoff target accepted or backup changed");
await File.WriteAllBytesAsync(launcher, approvedLauncher);
Console.WriteLine("UPDATE_REPLAY_BACKUP_AND_POST_CUTOFF_IDENTITY=PASS");
Require(File.Exists(Path.Combine(backup,"runtime/obsolete.dll")) && !File.Exists(Path.Combine(installRoot,"runtime/obsolete.dll")), "Backup/replacement membership failed");
Require(File.ReadAllText(Path.Combine(vaultRoot,"sentinel")) == "protected" && Directory.GetFiles(vaultRoot).Length == 1, "Vault touched");
try
{
    await new UpdateStateStore(state).PublishHandoffPendingAsync(op);
    throw new Exception("Post-cutoff operation returned to HandoffPending");
}
catch (InvalidOperationException) { }
Require((await new UpdateStateStore(state).LoadAsync())?.Cutoff == UpdateStartupCutoff.RecoveryRequired,
    "Post-cutoff regression changed durable state");
var distinct = Guid.NewGuid();
try
{
    await new UpdateStateStore(state).PublishHandoffPendingAsync(distinct);
    throw new Exception("New operation replaced unresolved recovery authority");
}
catch (InvalidOperationException) { }
Require((await new UpdateStateStore(state).LoadAsync())?.OperationId == op, "New operation displaced recovery authority");
Console.WriteLine("UPDATE_CUTOFF_MONOTONICITY=PASS");

var recoveryRoot = Path.Combine(root, "pre-cutoff-recovery");
var recoveryInstall = new InstallPaths(Path.Combine(recoveryRoot, "install"));
var recoveryState = new AppStatePaths(Path.Combine(recoveryRoot, "state"));
var recoveryOp = Guid.NewGuid();
var recoveryBackup = recoveryInstall.Root + ".backup." + recoveryOp.ToString("N");
Directory.CreateDirectory(Path.Combine(recoveryBackup, "runtime"));
File.WriteAllText(Path.Combine(recoveryBackup, "runtime/obsolete.dll"), "old");
await new UpdateStateStore(recoveryState).PublishHandoffPendingAsync(recoveryOp);
var recovery = new UpdateRecoveryStore(recoveryState);
await recovery.SaveAsync(new(recoveryOp, recoveryInstall.Root,
    recoveryInstall.Root + ".staged." + recoveryOp.ToString("N"), recoveryBackup,
    Path.Combine(recoveryState.UpdateStagingPath, recoveryOp.ToString("D")),
    manifest.ComputeAuthoritySha256(), "InstallMovedToBackup", DateTimeOffset.UtcNow));
var restored = await new UpdateStartupRecovery(recoveryState, recoveryInstall, vaultRoot).ReconcileAsync();
Require(restored.CanContinue && !restored.ReplacementInstalled &&
    File.Exists(Path.Combine(recoveryInstall.Root, "runtime/obsolete.dll")), "Interrupted replacement backup recovery failed");
Require(File.ReadAllText(Path.Combine(vaultRoot,"sentinel"))=="protected", "Recovery touched Vault");
// Execute the isolated helper as a separate process against operation-bound recovery evidence.
foreach (var afterCutoff in new[] { false, true })
{
    var externalRoot = Path.Combine(root, afterCutoff ? "external-forward" : "external-rollback");
    var externalInstall = new InstallPaths(Path.Combine(externalRoot, "install"));
    var externalState = new AppStatePaths(Path.Combine(externalRoot, "state"));
    var externalOp = Guid.NewGuid();
    var externalTools = externalState.GetUpdateToolsPath(externalOp);
    var externalPayload = Path.Combine(externalState.UpdateStagingPath, externalOp.ToString("D"));
    var externalBackup = externalInstall.Root + ".backup." + externalOp.ToString("N");
    Directory.CreateDirectory(externalTools);
    Directory.CreateDirectory(externalInstall.Root);
    File.WriteAllText(Path.Combine(externalInstall.Root, "old.marker"), "original");
    foreach (var path in Directory.EnumerateFiles(payload, "*", SearchOption.AllDirectories))
    {
        var dest = Path.Combine(externalPayload, Path.GetRelativePath(payload, path));
        Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
        File.Copy(path, dest);
    }
    var externalHandoff = new UpdateHandoff(externalOp, externalPayload, externalInstall.Root, manifest,
        vaultRoot, DateTimeOffset.UtcNow.AddMinutes(5));
    var encoded = JsonSerializer.SerializeToUtf8Bytes(externalHandoff, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase });
    var externalHandoffPath = Path.Combine(externalTools, "handoff.json");
    File.WriteAllBytes(externalHandoffPath, encoded);
    var externalStore = new UpdateStateStore(externalState);
    await externalStore.PublishHandoffPendingAsync(externalOp, handoffSha256: Hash(encoded), manifestAuthoritySha256: manifest.ComputeAuthoritySha256());
    UpdateBackupIdentity.RecordBeforeMove(externalTools, externalInstall.Root, Hash(encoded), CancellationToken.None);
    var before = ReadVaultInventory(externalInstall.Root);
    UpdatePathGuard.MoveSibling(externalInstall.Root, externalBackup);
    if (afterCutoff) await externalStore.PublishCatalogWriteStartedAsync(externalOp, externalBackup);
    await externalStore.PublishRecoveryRequiredAsync(externalOp, externalBackup);
    var blocked = await new UpdateStartupRecovery(externalState, externalInstall, vaultRoot).ReconcileAsync();
    Require(!blocked.CanContinue, "Recovery barrier opened before verified repair");
    Require((await externalStore.LoadAsync())?.RecoveryOrigin == (afterCutoff ? UpdateStartupCutoff.CatalogWriteStarted : UpdateStartupCutoff.HandoffPending), "Recovery origin was lost");
    var executable = Environment.GetEnvironmentVariable("NAUT_TEST_RECOVERY_EXECUTABLE");
    System.Diagnostics.ProcessStartInfo start;
    if (!string.IsNullOrWhiteSpace(executable))
    {
        var isolatedExe = Path.Combine(externalTools, Path.GetFileName(executable));
        File.Copy(executable, isolatedExe);
        start = new(isolatedExe) { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, WorkingDirectory = externalTools, CreateNoWindow = true };
        start.ArgumentList.Add("--recover"); start.ArgumentList.Add("--no-launch");
    }
    else
    {
        var helper = typeof(ReplacementEngine).Assembly.Location;
        var helperName = Path.GetFileName(helper);
        File.Copy(helper, Path.Combine(externalTools, helperName));
        var contracts = typeof(UpdateBackupIdentity).Assembly.Location;
        File.Copy(contracts, Path.Combine(externalTools, Path.GetFileName(contracts)));
        var config = Path.Combine(AppContext.BaseDirectory, "check.runtimeconfig.json");
        start = new("dotnet") { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, WorkingDirectory = externalTools, CreateNoWindow = true };
        foreach (var arg in new[] { "exec", "--runtimeconfig", config, Path.Combine(externalTools, helperName), "--recover", "--no-launch" })
            start.ArgumentList.Add(arg);
    }
    using var child = System.Diagnostics.Process.Start(start)!;
    var error = child.StandardError.ReadToEndAsync();
    await child.WaitForExitAsync();
    Require(child.ExitCode == 0, "External recovery failed: " + await error);
    if (afterCutoff)
    {
        Require(before.SequenceEqual(ReadVaultInventory(externalBackup)) && !File.Exists(Path.Combine(externalInstall.Root, "old.marker")), "Forward recovery changed backup or restored old binary");
        var result = await new UpdateStartupRecovery(externalState, externalInstall, vaultRoot).ReconcileAsync();
        Require(result.CanContinue && result.ReplacementInstalled, "Forward target failed startup verification");
    }
    else
    {
        Require(before.SequenceEqual(ReadVaultInventory(externalInstall.Root)) && !Directory.Exists(externalBackup), "External rollback restored wrong identity");
        var result = await new UpdateStartupRecovery(externalState, externalInstall, vaultRoot).ReconcileAsync();
        Require(result.CanContinue && !result.ReplacementInstalled && (await externalStore.LoadAsync())?.Cutoff == UpdateStartupCutoff.AbortedNoMutation, "Verified rollback did not clear recovery barrier");
    }
}

var normalHelper = Environment.GetEnvironmentVariable("NAUT_TEST_RECOVERY_EXECUTABLE");
var crashHelper = Environment.GetEnvironmentVariable("NAUT_TEST_KILL_EXECUTABLE");
if (!string.IsNullOrWhiteSpace(normalHelper) && !string.IsNullOrWhiteSpace(crashHelper))
{
    foreach (var phase in new[] { "BeforeBackupMove", "AfterBackupMove", "AfterInstallMove", "Concurrent" })
    {
        var testRoot = Path.Combine(root, "actual-" + phase);
        var testInstall = new InstallPaths(Path.Combine(testRoot, "install"));
        var testState = new AppStatePaths(Path.Combine(testRoot, "state"));
        var testOp = Guid.NewGuid();
        var tools = testState.GetUpdateToolsPath(testOp);
        var testPayload = Path.Combine(testState.UpdateStagingPath, testOp.ToString("D"));
        var testBackup = testInstall.Root + ".backup." + testOp.ToString("N");
        Directory.CreateDirectory(tools);
        Directory.CreateDirectory(testInstall.Root);
        File.WriteAllText(Path.Combine(testInstall.Root, "old.marker"), "original");
        foreach (var path in Directory.EnumerateFiles(payload, "*", SearchOption.AllDirectories))
        {
            var dest = Path.Combine(testPayload, Path.GetRelativePath(payload, path));
            Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
            File.Copy(path, dest);
        }
        var authority = new UpdateHandoff(testOp, testPayload, testInstall.Root, manifest,
            vaultRoot, DateTimeOffset.UtcNow.AddMinutes(5));
        var bytes = JsonSerializer.SerializeToUtf8Bytes(authority, new JsonSerializerOptions { PropertyNamingPolicy=JsonNamingPolicy.CamelCase });
        var pathToHandoff = Path.Combine(tools, "handoff.json");
        await File.WriteAllBytesAsync(pathToHandoff, bytes);
        var store = new UpdateStateStore(testState);
        await store.PublishHandoffPendingAsync(testOp, handoffSha256: Hash(bytes), manifestAuthoritySha256: manifest.ComputeAuthoritySha256());
        var original = ReadVaultInventory(testInstall.Root);
        var recoveryExe = Path.Combine(tools, Path.GetFileName(normalHelper));
        File.Copy(normalHelper, recoveryExe);
        System.Diagnostics.ProcessStartInfo Start(string executable, bool recover = false)
        {
            var info = new System.Diagnostics.ProcessStartInfo(executable) { UseShellExecute=false, RedirectStandardOutput=true, RedirectStandardError=true, CreateNoWindow=true, WorkingDirectory=tools };
            if (recover) { info.ArgumentList.Add("--recover"); }
            else foreach (var a in new[] { "--handoff", pathToHandoff, "--handoff-sha256", Hash(bytes), "--manifest-sha256", manifest.ComputeAuthoritySha256() }) info.ArgumentList.Add(a);
            info.ArgumentList.Add("--no-launch");
            return info;
        }
        if (phase == "Concurrent")
        {
            using var one = System.Diagnostics.Process.Start(Start(recoveryExe))!;
            using var two = System.Diagnostics.Process.Start(Start(recoveryExe))!;
            await Task.WhenAll(one.WaitForExitAsync(), two.WaitForExitAsync()).WaitAsync(TimeSpan.FromSeconds(60));
            Require((one.ExitCode == 0) != (two.ExitCode == 0), "Concurrent helpers did not elect exactly one replacement");
            Require(original.SequenceEqual(ReadVaultInventory(testBackup)), "Concurrent helper mutated original backup");
        }
        else
        {
            var info = Start(crashHelper);
            info.Environment["NAUT_UPDATER_KILL_PHASE"] = phase;
            using var child = System.Diagnostics.Process.Start(info)!;
            try
            {
                var line = await child.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(30));
                Require(line == "CRASH_POINT " + phase, "Updater did not reach " + phase + ": " + line);
                child.Kill(); await child.WaitForExitAsync();
                Console.WriteLine($"ACTUAL_UPDATER_KILL {phase} exit={child.ExitCode}");
            }
            finally { if (!child.HasExited) { child.Kill(); await child.WaitForExitAsync(); } }
            using var repaired = System.Diagnostics.Process.Start(Start(recoveryExe, true))!;
            await repaired.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(60));
            Require(repaired.ExitCode == 0, "Actual crash recovery failed: " + await repaired.StandardError.ReadToEndAsync());
            var result = await new UpdateStartupRecovery(testState, testInstall, vaultRoot).ReconcileAsync();
            Require(result.CanContinue && result.ReplacementInstalled == (phase == "AfterInstallMove"), "Recovered topology rejected");
            Require(original.SequenceEqual(ReadVaultInventory(phase == "AfterInstallMove" ? testBackup : testInstall.Root)), "Original bytes lost after actual kill");
        }
        Require(File.ReadAllText(sentinelPath) == "protected", "Actual crash or competing helper touched Vault");
        Console.WriteLine("ACTUAL_UPDATER_RECOVERY " + phase + "=PASS");
    }
}
Console.WriteLine("UPDATE_EXTERNAL_HELPER_MISSING_ROOT_ROLLBACK_FORWARD=PASS");
Console.WriteLine("UPDATE_RECOVERY_REQUIRED_ORIGIN_AND_VERIFIED_REPAIR=PASS");
Console.WriteLine("PLANNER_STAGING_DOWNLOAD_FALLBACK_PROGRESS_REPLACEMENT_ISOLATION_MPG_MPEG=PASS");

static string ReadInstalledVersion(string installation)
{
    var manifest = UpdateReleaseManifest.Parse(File.ReadAllText(Path.Combine(installation, "release-manifest.json")));
    var context = new System.Runtime.Loader.AssemblyLoadContext("installed-version", true);
    try
    {
        using var runtimeBytes = new MemoryStream(File.ReadAllBytes(Path.Combine(installation, "runtime", "Neuterradise.Runtime.dll")));
        var runtime = context.LoadFromStream(runtimeBytes);
        var embedded = runtime.GetType("Neuterradise.App.SystemServices.ProductIdentity")?.GetProperty("Version")?.GetValue(null) as string;
        Require(embedded == manifest.ProductVersion, "Embedded install version differs from release manifest.");
        return manifest.ProductVersion;
    }
    finally { context.Unload(); }
}

static KeyValuePair<string, string>[] ReadVaultInventory(string root) =>
    Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories).Order(StringComparer.Ordinal)
        .Select(path => KeyValuePair.Create(Path.GetRelativePath(root, path), Hash(File.ReadAllBytes(path)))).ToArray();

sealed class Recorder : IProgress<UpdateProgress>
{
    public List<UpdateProgress> Values { get; } = new();
    public void Report(UpdateProgress value) => Values.Add(value);
}
sealed class Canceller(CancellationTokenSource source) : IProgress<UpdateProgress>
{
    public void Report(UpdateProgress value) { if (value.CompletedBytes > 0) source.Cancel(); }
}
sealed class FixtureHandler(Func<HttpRequestMessage,byte[]> body, string? fault = null) : HttpMessageHandler
{
    public List<Uri> Requests { get; } = new();
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken token)
    {
        Requests.Add(request.RequestUri!);
        if (fault == "network") throw new HttpRequestException("fixture network unavailable");
        var bytes = body(request);
        if (fault == "corrupt") bytes = Encoding.UTF8.GetBytes("xyz");
        if (fault == "partial") bytes = Encoding.UTF8.GetBytes("ab");
        var response = new HttpResponseMessage(fault == "missing" ? HttpStatusCode.NotFound : HttpStatusCode.OK)
        { RequestMessage = fault == "redirect" ? new HttpRequestMessage(HttpMethod.Get,"https://evil.example/test") : request,
          Content = new ByteArrayContent(bytes) };
        if (fault == "partial") response.Content.Headers.ContentLength=null;
        if (fault == "length") response.Content.Headers.ContentLength=4;
        return Task.FromResult(response);
    }
}
