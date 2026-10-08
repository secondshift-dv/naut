using System.Net;
using System.Security.Cryptography;
using Neuterradise.App.SystemServices;
using Neuterradise.App.SystemServices.Storage;
using Neuterradise.App.SystemServices.Updates;
using Neuterradise.Release.Contracts;

var dist = Path.GetFullPath(args[0]);
var fixture = Path.GetFullPath(args[1]);
var feed = new Uri("https://github.com/secondshift-dv/naut/releases/latest/download/update.json");
var manifest = UpdateManifest.Parse(File.ReadAllText(Path.Combine(dist,"update.json")));
if (manifest.ProductVersion != "0.0.3") throw new Exception("Legacy contract requires exact target 0.0.3");
var signature = Path.Combine(dist,"update-signature.json");
var preflight = args.Length > 2 && args[2] == "--preflight";
if (args.Length > 2 && !preflight) signature = Path.GetFullPath(args[2]);
using var http = new HttpClient(new AssetHandler(dist, signature));
if (!preflight)
{
    var result = await new UpdateCheckService(http).CheckAsync(feed,new(feed.AbsoluteUri,false));
    if (!result.IsAvailable || result.Manifest is null || result.Decision?.Accepted!=true)
        throw new Exception("Legacy check rejected target: " + result.SafeError);
}
else if (!new UpdateTrustPolicy().Evaluate(manifest,new(feed.AbsoluteUri,false),false,false,true).Accepted)
    throw new Exception("Legacy version/runtime contract rejected target");
var state = new AppStatePaths(Path.Combine(fixture,"state"));
var install = new InstallPaths(Path.Combine(fixture,"install"));
var name = $"naut-v{manifest.ProductVersion}-{manifest.RuntimeIdentifier}.zip";
var payloadUri = new Uri(feed,name);
var op = Guid.NewGuid();
var download = await new UpdateDownloadService(http,state).DownloadAsync(payloadUri,op,manifest.PayloadByteLength,manifest.PayloadSha256);
if (!download.IsSuccess) throw new Exception("Legacy payload rejected: " + download.SafeError);
var staged = await new UpdatePackageStager(state,new UpdatePackageValidator(install)).ExtractAndValidateAsync(download.PayloadPath!,op,manifest);
if (!staged.IsAccepted) throw new Exception("Legacy staging rejected target: " + staged.SafeError);
if (manifest.MinimumCompatibleVersion is not null && Version.Parse(manifest.MinimumCompatibleVersion)>Version.Parse(ProductIdentity.Version))
    throw new Exception("Intermediate version required");
if (!preflight)
{
    var mutated = File.ReadAllBytes(Path.Combine(dist,"update.json"));
    mutated[mutated.Length-1]^=1;
    if (new UpdatePublisherSignatureVerifier().Verify(mutated, File.ReadAllBytes(signature)).Verified)
        throw new Exception("Legacy publisher validation accepted tampering");
}
Console.WriteLine(preflight
    ? $"DIRECT_{ProductIdentity.Version}_TO_0.0.3_TARGET_LAYOUT_PAYLOAD=PASS;TARGET_SIGNATURE=DEFERRED_TO_SIGNING_GATE"
    : $"DIRECT_{ProductIdentity.Version}_TO_0.0.3_CHECK_SIGNATURE_ZIP_STAGING=PASS");

sealed class AssetHandler(string dist,string signature) : HttpMessageHandler
{
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken cancellationToken)
    {
        var name=Path.GetFileName(request.RequestUri!.AbsolutePath);
        var path=name=="update-signature.json"?signature:Path.Combine(dist,name);
        var response=new HttpResponseMessage(HttpStatusCode.OK) { RequestMessage=request,
            Content=new StreamContent(File.OpenRead(path)) };
        response.Content.Headers.ContentLength=new FileInfo(path).Length;
        return Task.FromResult(response);
    }
}
