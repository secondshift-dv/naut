using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Neuterradise.App.SystemServices.Storage;

namespace Neuterradise.App.Media.Model;

public enum ModelPackageFormat { Gltf, Glb, Obj, Dae, Fbx, Blend, Stl, ThreeMf }

public enum ComponentRole { Primary, Dependency }

public sealed record ModelPackageFile(
    string RelativePath,
    long ByteLength,
    string Sha256,
    ComponentRole Role = ComponentRole.Dependency)
{
    public void Validate()
    {
        if (string.IsNullOrWhiteSpace(RelativePath)
            || Path.IsPathRooted(RelativePath)
            || RelativePath.Contains("..", StringComparison.Ordinal)
            || RelativePath.Contains(':'))
        {
            throw new FormatException("Model dependency path is unsafe.");
        }

        if (ByteLength < 0
            || Sha256.Length != 64
            || Sha256.Any(c => !char.IsAsciiDigit(c) && c is not (>= 'a' and <= 'f')))
        {
            throw new FormatException("Model dependency identity is invalid.");
        }
    }
}

public sealed record DiscoveredComponent(
    string RelativePath,
    string NormalizedRelativePath,
    ComponentRole Role,
    long ByteLength,
    string Sha256,
    string? OriginalSourcePath,
    bool Exists);

public enum DependencyDiscoveryState
{
    Complete,
    MissingDependencies,
    Unknown,
    FailedRetryable,
    FailedTerminal,
    Unsupported,
}

public sealed record ModelPackageDiscoveryResult(
    ModelPackageFormat Format,
    MediaDependencyStatus DependencyStatus,
    DependencyDiscoveryState DiscoveryState,
    string? BundleSha256,
    IReadOnlyList<DiscoveredComponent> Components)
{
    public bool IsComplete =>
        DiscoveryState == DependencyDiscoveryState.Complete
        && DependencyStatus is MediaDependencyStatus.Complete or MediaDependencyStatus.SelfContained;

    public bool HasMissingDependencies =>
        DiscoveryState == DependencyDiscoveryState.MissingDependencies
        || DependencyStatus == MediaDependencyStatus.DependenciesMissing;

    public bool HasUnknownDependencies =>
        DiscoveryState is DependencyDiscoveryState.Unknown
            or DependencyDiscoveryState.FailedRetryable
            or DependencyDiscoveryState.FailedTerminal
            or DependencyDiscoveryState.Unsupported
        || DependencyStatus == MediaDependencyStatus.DependenciesUnknown;
}

public sealed record ModelPackagePlan(
    Guid MediaId,
    ModelPackageFormat Format,
    IReadOnlyList<ModelPackageFile> Files,
    string BundleSha256)
{
    public void Validate(string packageRoot)
    {
        if (MediaId == Guid.Empty || Files is null || Files.Count == 0)
        {
            throw new FormatException("Model package plan is incomplete.");
        }

        var root = Path.GetFullPath(packageRoot);
        foreach (var file in Files)
        {
            file.Validate();
            var path = RootPathRules.ResolveContainedPath(root, root, file.RelativePath, nameof(packageRoot));
            if (!File.Exists(path))
            {
                throw new FileNotFoundException("Model dependency is missing.", path);
            }

            var info = new FileInfo(path);
            if (info.Length != file.ByteLength || !string.Equals(Hash(path), file.Sha256, StringComparison.Ordinal))
            {
                throw new InvalidDataException("Model dependency changed after planning.");
            }
        }

        if (BundleSha256.Length != 64 || !string.Equals(HashBundle(Files), BundleSha256, StringComparison.Ordinal))
        {
            throw new InvalidDataException("Model bundle identity is invalid.");
        }
    }

    public static string Hash(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexStringLower(SHA256.HashData(stream));
    }

    /// <summary>
    /// Computes the canonical bundle SHA256 according to the product contract:
    /// sort normalized relative path ordinal, concat UTF-8 (path + NUL + hash + NUL + length + LF), SHA-256.
    /// </summary>
    public static string HashBundle(IReadOnlyList<ModelPackageFile> files)
    {
        ArgumentNullException.ThrowIfNull(files);
        var sorted = files
            .Select(f => (NormPath: NormalizeComponentPath(f.RelativePath), File: f))
            .OrderBy(x => x.NormPath, StringComparer.Ordinal);

        var ms = new MemoryStream();
        foreach (var item in sorted)
        {
            var line = $"{item.NormPath}\0{item.File.Sha256.ToLowerInvariant()}\0{item.File.ByteLength}\n";
            var bytes = Encoding.UTF8.GetBytes(line);
            ms.Write(bytes, 0, bytes.Length);
        }

        ms.Position = 0;
        return Convert.ToHexStringLower(SHA256.HashData(ms.ToArray()));
    }

    public static string HashBundle(IReadOnlyList<DiscoveredComponent> components)
    {
        ArgumentNullException.ThrowIfNull(components);
        var sorted = components
            .Where(c => c.Exists)
            .Select(c => (NormPath: NormalizeComponentPath(c.NormalizedRelativePath), Comp: c))
            .OrderBy(x => x.NormPath, StringComparer.Ordinal);

        var ms = new MemoryStream();
        foreach (var item in sorted)
        {
            var line = $"{item.NormPath}\0{item.Comp.Sha256.ToLowerInvariant()}\0{item.Comp.ByteLength}\n";
            var bytes = Encoding.UTF8.GetBytes(line);
            ms.Write(bytes, 0, bytes.Length);
        }

        ms.Position = 0;
        return Convert.ToHexStringLower(SHA256.HashData(ms.ToArray()));
    }

    public static string NormalizeComponentPath(string path) =>
        string.Join('/', path.Normalize(NormalizationForm.FormC).Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries));
}

public sealed record ModelPackageLifecycle(ModelPackagePlan Plan, string State, string? RetirementReason);

public static class ModelPackageDiscovery
{
    public static ModelPackageDiscoveryResult Discover(
        string primaryFilePath,
        string? knownPrimarySha256 = null,
        long? knownPrimaryByteLength = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(primaryFilePath);
        var fullPath = Path.GetFullPath(primaryFilePath);
        var packageDir = Path.GetDirectoryName(fullPath) ?? string.Empty;
        var primaryFileName = Path.GetFileName(fullPath);
        var ext = Path.GetExtension(fullPath).ToLowerInvariant();

        var format = ext switch
        {
            ".gltf" => ModelPackageFormat.Gltf,
            ".glb" => ModelPackageFormat.Glb,
            ".obj" => ModelPackageFormat.Obj,
            ".dae" => ModelPackageFormat.Dae,
            ".fbx" => ModelPackageFormat.Fbx,
            ".blend" => ModelPackageFormat.Blend,
            ".stl" => ModelPackageFormat.Stl,
            ".3mf" => ModelPackageFormat.ThreeMf,
            _ => ModelPackageFormat.Obj,
        };

        if (!File.Exists(fullPath))
        {
            return new ModelPackageDiscoveryResult(
                format,
                MediaDependencyStatus.DependenciesMissing,
                DependencyDiscoveryState.MissingDependencies,
                null,
                Array.Empty<DiscoveredComponent>());
        }

        var primaryInfo = new FileInfo(fullPath);
        var primaryLength = knownPrimaryByteLength ?? primaryInfo.Length;
        var primarySha = knownPrimarySha256 ?? ModelPackagePlan.Hash(fullPath);
        var primaryComp = new DiscoveredComponent(
            primaryFileName,
            ModelPackagePlan.NormalizeComponentPath(primaryFileName),
            ComponentRole.Primary,
            primaryLength,
            primarySha,
            fullPath,
            true);

        if (format is ModelPackageFormat.Glb or ModelPackageFormat.Stl or ModelPackageFormat.ThreeMf)
        {
            return new ModelPackageDiscoveryResult(
                format,
                MediaDependencyStatus.SelfContained,
                DependencyDiscoveryState.Complete,
                primarySha,
                [primaryComp]);
        }

        if (format is ModelPackageFormat.Fbx or ModelPackageFormat.Blend)
        {
            return new ModelPackageDiscoveryResult(
                format,
                MediaDependencyStatus.DependenciesUnknown,
                DependencyDiscoveryState.Unsupported,
                null,
                [primaryComp]);
        }

        var discoveredDeps = new Dictionary<string, string>(StringComparer.Ordinal);
        try
        {
            if (format == ModelPackageFormat.Gltf)
            {
                DiscoverGltfDependencies(fullPath, discoveredDeps);
            }
            else if (format == ModelPackageFormat.Obj)
            {
                DiscoverObjDependencies(fullPath, packageDir, discoveredDeps);
            }
            else if (format == ModelPackageFormat.Dae)
            {
                DiscoverDaeDependencies(fullPath, discoveredDeps);
            }
        }
        catch (UnauthorizedAccessException)
        {
            return Failure(format, primaryComp, DependencyDiscoveryState.FailedRetryable);
        }
        catch (IOException)
        {
            return Failure(format, primaryComp, DependencyDiscoveryState.FailedRetryable);
        }
        catch (JsonException)
        {
            return Failure(format, primaryComp, DependencyDiscoveryState.FailedTerminal);
        }
        catch (UnsupportedDependencyReferenceException)
        {
            return Failure(format, primaryComp, DependencyDiscoveryState.Unsupported);
        }
        catch (FormatException)
        {
            return Failure(format, primaryComp, DependencyDiscoveryState.FailedTerminal);
        }

        var components = new List<DiscoveredComponent> { primaryComp };
        var hasMissing = false;

        foreach (var (relPath, normPath) in discoveredDeps)
        {
            var depFullPath = Path.Combine(packageDir, relPath.Replace('/', Path.DirectorySeparatorChar));
            if (File.Exists(depFullPath))
            {
                var depInfo = new FileInfo(depFullPath);
                components.Add(new DiscoveredComponent(
                    relPath,
                    normPath,
                    ComponentRole.Dependency,
                    depInfo.Length,
                    ModelPackagePlan.Hash(depFullPath),
                    depFullPath,
                    true));
            }
            else
            {
                hasMissing = true;
                components.Add(new DiscoveredComponent(
                    relPath,
                    normPath,
                    ComponentRole.Dependency,
                    0,
                    new string('0', 64),
                    depFullPath,
                    false));
            }
        }

        var status = components.Count == 1
            ? MediaDependencyStatus.SelfContained
            : hasMissing
                ? MediaDependencyStatus.DependenciesMissing
                : MediaDependencyStatus.Complete;
        var discoveryState = hasMissing
            ? DependencyDiscoveryState.MissingDependencies
            : DependencyDiscoveryState.Complete;
        var bundleSha = discoveryState == DependencyDiscoveryState.Complete
            ? ModelPackagePlan.HashBundle(components)
            : null;

        return new ModelPackageDiscoveryResult(format, status, discoveryState, bundleSha, components);
    }

    private static ModelPackageDiscoveryResult Failure(
        ModelPackageFormat format,
        DiscoveredComponent primary,
        DependencyDiscoveryState state) =>
        new(format, MediaDependencyStatus.DependenciesUnknown, state, null, [primary]);

    private static void DiscoverGltfDependencies(string gltfPath, Dictionary<string, string> deps)
    {
        using var stream = File.OpenRead(gltfPath);
        using var doc = JsonDocument.Parse(stream);
        var root = doc.RootElement;

        if (root.TryGetProperty("buffers", out var buffers) && buffers.ValueKind == JsonValueKind.Array)
        {
            foreach (var buffer in buffers.EnumerateArray())
            {
                if (buffer.TryGetProperty("uri", out var uri) && uri.ValueKind == JsonValueKind.String)
                {
                    AddSafeUri(uri.GetString(), true, deps);
                }
            }
        }

        if (root.TryGetProperty("images", out var images) && images.ValueKind == JsonValueKind.Array)
        {
            foreach (var image in images.EnumerateArray())
            {
                if (image.TryGetProperty("uri", out var uri) && uri.ValueKind == JsonValueKind.String)
                {
                    AddSafeUri(uri.GetString(), true, deps);
                }
            }
        }
    }

    private static void DiscoverObjDependencies(string objPath, string packageDir, Dictionary<string, string> deps)
    {
        var mtlFiles = new List<string>();
        foreach (var line in File.ReadLines(objPath))
        {
            var trimmed = line.Trim();
            if (trimmed.Length == 0 || trimmed.StartsWith("#")) continue;
            var tokens = trimmed.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            if (tokens.Length >= 2 && string.Equals(tokens[0], "mtllib", StringComparison.OrdinalIgnoreCase))
            {
                for (var i = 1; i < tokens.Length; i++)
                {
                    if (AddSafeUri(tokens[i], false, deps))
                    {
                        mtlFiles.Add(tokens[i]);
                    }
                }
            }
        }

        foreach (var mtlName in mtlFiles)
        {
            var mtlPath = Path.Combine(packageDir, mtlName.Replace('/', Path.DirectorySeparatorChar));
            if (!File.Exists(mtlPath)) continue;

            foreach (var line in File.ReadLines(mtlPath))
            {
                var trimmed = line.Trim();
                if (trimmed.Length == 0 || trimmed.StartsWith("#")) continue;
                var tokens = trimmed.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
                if (tokens.Length < 2) continue;
                var directive = tokens[0].ToLowerInvariant();
                if (directive is "map_kd" or "map_bump" or "bump" or "map_d" or "map_ks" or "map_ka" or "map_ns" or "disp" or "decal")
                {
                    AddSafeUri(tokens[^1], false, deps);
                }
            }
        }
    }

    private static void DiscoverDaeDependencies(string daePath, Dictionary<string, string> deps)
    {
        var text = File.ReadAllText(daePath);
        var regex = new Regex(@"<init_from>\s*(.*?)\s*</init_from>", RegexOptions.IgnoreCase | RegexOptions.Singleline);
        foreach (Match match in regex.Matches(text))
        {
            var value = match.Groups[1].Value.Trim();
            if (!string.IsNullOrEmpty(value))
            {
                AddSafeUri(value, true, deps);
            }
        }
    }

    private static bool AddSafeUri(string? uri, bool unescape, Dictionary<string, string> deps)
    {
        if (string.IsNullOrWhiteSpace(uri))
        {
            return false;
        }

        if (uri.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
        {
            // Embedded payloads are self-contained and intentionally do not create a sidecar.
            return false;
        }

        if (uri.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
            || uri.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            throw new UnsupportedDependencyReferenceException(
                "Remote model dependencies are not supported as durable package authority.");
        }

        var candidate = (unescape ? Uri.UnescapeDataString(uri) : uri).Trim().Replace('\\', '/');
        if (Path.IsPathRooted(candidate)
            || candidate.Contains(':')
            || candidate.Split('/').Any(segment => segment is ".." or "."))
        {
            throw new FormatException("Model dependency reference is outside the package boundary.");
        }

        var normalized = ModelPackagePlan.NormalizeComponentPath(candidate);
        if (string.IsNullOrEmpty(normalized))
        {
            throw new FormatException("Model dependency reference is empty after normalization.");
        }

        deps[candidate] = normalized;
        return true;
    }

    private sealed class UnsupportedDependencyReferenceException : Exception
    {
        public UnsupportedDependencyReferenceException(string message)
            : base(message)
        {
        }
    }
}
