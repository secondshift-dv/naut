using System.Security.Cryptography;
using System.Text.Json;
using Neuterradise.App.SystemServices.Storage;
using Neuterradise.App.SystemServices.Updates;
using Neuterradise.Release.Contracts;

var fixture = Path.GetFullPath(args[0]);
Directory.CreateDirectory(fixture);
var contract = ReleaseContract.Current;
void Require(bool passed, string detail)
{
    if (!passed) throw new InvalidOperationException(detail);
}
foreach (var legacy in new[] { false, true })
{
    var root = Path.Combine(fixture, legacy ? "legacy" : "current");
    var members = contract.RequiredMembers.Select(path => ReleaseLayout.RequiredMember(path, legacy))
        .Append(ReleaseLayout.RuntimeDirectory(legacy) + "/OpenCvSharpExtern.dll").ToArray();
    var files = new List<UpdateManifestFile>();
    foreach (var member in members)
    {
        var path = Path.Combine(root, member.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var bytes = System.Text.Encoding.UTF8.GetBytes("fixture:" + member);
        File.WriteAllBytes(path, bytes);
        files.Add(new UpdateManifestFile(member, bytes.Length, Convert.ToHexStringLower(SHA256.HashData(bytes)), "runtime"));
    }
    var manifest = new UpdateManifest(1, contract.ProductId, "0.0.1", contract.RuntimeIdentifier,
        0, new string('0', 64), null, files);
    var control = new UpdateReleaseManifest(1, contract.ProductId, "0.0.1", contract.RuntimeIdentifier, files);
    File.WriteAllText(Path.Combine(root, ReleaseLayout.ReleaseManifestFileName), JsonSerializer.Serialize(control));
    var install = new InstallPaths(root);
    var validator = new UpdatePackageValidator(install);
    var validation = validator.Validate(manifest, root);
    Require(validation.IsAccepted, "Supported payload rejected: " + validation.SafeError);
    Require(Path.TrimEndingDirectorySeparator(install.RuntimeRoot) == Path.Combine(root, ReleaseLayout.RuntimeDirectory(legacy)), "Runtime root resolution failed");
    Require(install.AppExecutablePath == Path.Combine(root, ReleaseLayout.LauncherName(legacy)), "Launcher resolution failed");
    Require(install.RuntimeAppExecutablePath == Path.Combine(root, ReleaseLayout.RuntimeDirectory(legacy), ReleaseLayout.LauncherName(legacy)), "Runtime executable resolution failed");
    var extra = Path.Combine(root, "unexpected.dll");
    File.WriteAllText(extra, "unexpected");
    Require(!validator.Validate(manifest, root).IsAccepted, "Unapproved extra payload accepted");
    File.Delete(extra);
    var mixedMember = ReleaseLayout.LauncherName(!legacy);
    File.WriteAllText(Path.Combine(root, mixedMember), "mixed");
    var mixedFiles = files.Append(new UpdateManifestFile(mixedMember, 5,
        Convert.ToHexStringLower(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes("mixed"))), "launcher")).ToArray();
    Require(!validator.Validate(manifest with { Files = mixedFiles }, root).IsAccepted, "Mixed package layout accepted");
    File.Delete(Path.Combine(root, mixedMember));
    File.AppendAllText(Path.Combine(root, members[0]), "changed");
    Require(!validator.Validate(manifest, root).IsAccepted, "Tampered launcher accepted");
}
Console.WriteLine("RELEASE_LAYOUT_CURRENT_LEGACY=PASS");
