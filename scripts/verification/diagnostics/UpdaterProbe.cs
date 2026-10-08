using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Neuterradise.App.Import.Intake;
using Neuterradise.App.Media;
using Neuterradise.App.SystemServices.Storage;
using Neuterradise.App.SystemServices.Updates;
using Neuterradise.Release.Contracts;
using Neuterradise.Updater;

static void Require(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
static string Hash(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));
var root = Path.GetFullPath(args[0]);
Directory.CreateDirectory(root);
if (args.Length > 1)
{
    var context = new System.Runtime.Loader.AssemblyLoadContext("packaged", true);
    var runtime = context.LoadFromAssemblyPath(Path.Combine(Path.GetFullPath(args[1]), "runtime", "Neuterradise.Runtime.dll"));
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
    .Append(IncrementalUpdateService.MarkerPath).Append("runtime/new.dll").Append("runtime/empty.marker").ToArray();
foreach (var member in members)
{
    var bytes = member == IncrementalUpdateService.MarkerPath ? Encoding.UTF8.GetBytes("{\"schemaVersion\":1}")
        : member == "runtime/empty.marker" ? Array.Empty<byte>() : Encoding.UTF8.GetBytes("target:" + member);
    files.Add(new(member, bytes.Length, Hash(bytes), "runtime"));
    data[Hash(bytes)] = bytes;
    if (member is "runtime/new.dll" or "runtime/empty.marker") continue;
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
Require(plan.DownloadFiles.Count == 4 && plan.DownloadFiles.Contains(changed) && plan.DownloadFiles.Contains(corrupt)
    && plan.DownloadFiles.Any(f => f.RelativePath == "runtime/new.dll"), "Planner reused corrupt/changed bytes or missed new file");
Require(plan.ReusedFiles.Count == files.Count - 4, "Unchanged file scheduled for transfer");
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
Require(points[^1].CompletedBytes == plan.DownloadBytes && points[^1].CompletedFiles == 4, "Final progress incomplete");
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
Require(File.Exists(Path.Combine(backup,"runtime/obsolete.dll")) && !File.Exists(Path.Combine(installRoot,"runtime/obsolete.dll")), "Backup/replacement membership failed");
Require(File.ReadAllText(Path.Combine(vaultRoot,"sentinel")) == "protected" && Directory.GetFiles(vaultRoot).Length == 1, "Vault touched");
var recovery = new UpdateRecoveryStore(state);
await recovery.SaveAsync(new(op, installRoot, installRoot+".staged."+op.ToString("N"), backup, payload,
    manifest.ComputeAuthoritySha256(), "InstallMovedToBackup", DateTimeOffset.UtcNow));
await new UpdateStateStore(state).PublishHandoffPendingAsync(op);
Directory.Move(installRoot, Path.Combine(root,"interrupted-replacement"));
var restored = await new UpdateStartupRecovery(state, install, vaultRoot).ReconcileAsync();
Require(restored.CanContinue && !restored.ReplacementInstalled && File.Exists(Path.Combine(installRoot,"runtime/obsolete.dll")), "Interrupted replacement backup recovery failed");
Require(File.ReadAllText(Path.Combine(vaultRoot,"sentinel"))=="protected", "Recovery touched Vault");
Console.WriteLine("PLANNER_STAGING_DOWNLOAD_FALLBACK_PROGRESS_REPLACEMENT_ISOLATION_MPG_MPEG=PASS");

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
