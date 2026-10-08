using System.Security.Cryptography;
using System.Text.Json;
using Neuterradise.App.SystemServices.Database.Reads;
using Neuterradise.App.SystemServices.MediaTools;
using Neuterradise.App.SystemServices.Storage;

namespace Neuterradise.App.Media.Model;

public static class ModelFigureConversion
{
    public const string ToolId = "model-converter";

    public static async Task<string> ConvertAsync(MediaSource source, MediaReads reads, VaultPaths paths,
        ExternalToolResolver tools, IProcessLauncher launcher, string directory, CancellationToken ct)
    {
        var executable = tools.TryResolveExecutablePath(ToolId)
            ?? throw new NotSupportedException("The approved model converter is unavailable. Repair the installed application.");
        var original = source.ResolveManagedPath(paths)
            ?? throw new InvalidDataException("Canonical model bytes are unavailable.");
        var components = await reads.GetMediaComponentsAsync(source.MediaId, ct).ConfigureAwait(false);
        var files = new List<ConversionFile> { new(Path.GetFileName(original), original,
            source.Sha256 ?? throw new InvalidDataException("Model provenance is missing.")) };
        foreach (var component in components.Where(component => component.ComponentRole == ComponentRole.Dependency))
        {
            var canonical = paths.ResolveVaultRelativePath(source.CurrentManagedRelativePath + "/" + component.ComponentRelativePath);
            files.Add(new(component.ComponentRelativePath, canonical, component.Sha256));
        }
        if (files.Count > 256) throw new InvalidDataException("Model component budget exceeded.");
        foreach (var file in files)
            await VerifyAsync(file, ct).ConfigureAwait(false);
        Directory.CreateDirectory(directory);
        var manifest = Path.Combine(directory, "model-input.json");
        var output = Path.Combine(directory, "converted.glb");
        await File.WriteAllTextAsync(manifest, JsonSerializer.Serialize(new ConversionInput(Path.GetFileName(original), files.ToArray())), ct)
            .ConfigureAwait(false);
        var result = await launcher.RunAsync(new ProcessRunRequest(executable, [manifest, output],
            Timeout: TimeSpan.FromMinutes(2)), ct).ConfigureAwait(false);
        if (result.TimedOut) throw new InvalidDataException("Model conversion exceeded its time budget.");
        if (result.ExitCode != 0) throw new InvalidDataException("Model conversion failed: " + result.StandardError);
        if (!File.Exists(output) || new FileInfo(output).Length is < 20 or > 256 * 1024 * 1024)
            throw new InvalidDataException("Model conversion produced no bounded scene.");
        foreach (var file in files)
            await VerifyAsync(file, ct).ConfigureAwait(false);
        return output;
    }

    private static async Task VerifyAsync(ConversionFile file, CancellationToken ct)
    {
        await using var stream = new FileStream(file.Path, FileMode.Open, FileAccess.Read, FileShare.Read,
            65536, FileOptions.Asynchronous | FileOptions.SequentialScan);
        if (stream.Length is < 1 or > 256 * 1024 * 1024)
            throw new InvalidDataException("Model component exceeds its byte budget.");
        var hash = Convert.ToHexStringLower(await SHA256.HashDataAsync(stream, ct).ConfigureAwait(false));
        if (hash != file.Sha256) throw new InvalidDataException("Canonical model component provenance changed.");
    }

    private sealed record ConversionInput(string Primary, ConversionFile[] Files);
    private sealed record ConversionFile(string Name, string Path, string Sha256);
}
