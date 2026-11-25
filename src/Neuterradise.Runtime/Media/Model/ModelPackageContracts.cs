using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Neuterradise.App.SystemServices.Storage;

namespace Neuterradise.App.Media.Model;

public enum ModelPackageFormat { Gltf, Glb, Obj, Dae, Fbx, Blend, Stl, ThreeMf, Ply }

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
        long? knownPrimaryByteLength = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(primaryFilePath);
        cancellationToken.ThrowIfCancellationRequested();
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
            ".ply" => ModelPackageFormat.Ply,
            _ => throw new NotSupportedException("Unrecognized model package format."),
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

        RootPathRules.RejectExistingReparsePoints(Path.GetPathRoot(fullPath)!, fullPath);
        var primaryInfo = new FileInfo(fullPath);
        var primaryLength = knownPrimaryByteLength ?? primaryInfo.Length;
        var primarySha = knownPrimarySha256 ?? HashComponent(fullPath, cancellationToken);
        var primaryComp = new DiscoveredComponent(
            primaryFileName,
            ModelPackagePlan.NormalizeComponentPath(primaryFileName),
            ComponentRole.Primary,
            primaryLength,
            primarySha,
            fullPath,
            true);

        if (format is ModelPackageFormat.Glb or ModelPackageFormat.Stl or ModelPackageFormat.ThreeMf or ModelPackageFormat.Ply)
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
                DiscoverGltfDependencies(fullPath, discoveredDeps, cancellationToken);
            }
            else if (format == ModelPackageFormat.Obj)
            {
                DiscoverObjDependencies(fullPath, packageDir, discoveredDeps, cancellationToken);
            }
            else if (format == ModelPackageFormat.Dae)
            {
                DiscoverDaeDependencies(fullPath, discoveredDeps, cancellationToken);
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
        catch (RegexMatchTimeoutException)
        {
            return Failure(format, primaryComp, DependencyDiscoveryState.Unsupported);
        }

        var components = new List<DiscoveredComponent> { primaryComp };
        var hasMissing = false;

        foreach (var (relPath, normPath) in discoveredDeps)
        {
            cancellationToken.ThrowIfCancellationRequested();
            string depFullPath;
            try { depFullPath = RootPathRules.ResolveContainedPath(packageDir, packageDir, relPath, nameof(relPath)); }
            catch (IOException) { return Failure(format, primaryComp, DependencyDiscoveryState.Unsupported); }
            catch (ArgumentException) { return Failure(format, primaryComp, DependencyDiscoveryState.Unsupported); }
            if (File.Exists(depFullPath))
            {
                var depInfo = new FileInfo(depFullPath);
                components.Add(new DiscoveredComponent(
                    relPath,
                    normPath,
                    ComponentRole.Dependency,
                    depInfo.Length,
                    HashComponent(depFullPath, cancellationToken),
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

    private const long MaxDiscoveryDocumentBytes = 32L * 1024 * 1024;
    private const int MaxDependencies = 4096;

    private static FileStream OpenPackageFile(string path)
    {
        RootPathRules.RejectExistingReparsePoints(Path.GetPathRoot(path)!, path);
        var stream = File.OpenRead(path);
        if (SourceIdentityHelper.MatchesOpenedPath(stream.SafeFileHandle, path)) return stream;
        stream.Dispose();
        throw new IOException("The opened model package file resolves outside its declared path.");
    }

    private static IEnumerable<string> ReadPackageLines(string path)
    {
        using var stream = OpenPackageFile(path);
        using var reader = new StreamReader(stream);
        while (reader.ReadLine() is { } line) yield return line;
    }

    private static string HashComponent(string path, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        using var stream = OpenPackageFile(path);
        return Convert.ToHexStringLower(SHA256.HashDataAsync(stream, token).GetAwaiter().GetResult());
    }

    private static void CheckDocumentSize(string path)
    {
        if (new FileInfo(path).Length > MaxDiscoveryDocumentBytes)
            throw new UnsupportedDependencyReferenceException("Model dependency document exceeds the discovery limit.");
    }

    private static void DiscoverGltfDependencies(string gltfPath, Dictionary<string, string> deps, CancellationToken token)
    {
        CheckDocumentSize(gltfPath);
        token.ThrowIfCancellationRequested();
        using var stream = OpenPackageFile(gltfPath);
        using var doc = JsonDocument.ParseAsync(stream, cancellationToken: token).GetAwaiter().GetResult();
        var root = doc.RootElement;

        if (root.TryGetProperty("buffers", out var buffers) && buffers.ValueKind == JsonValueKind.Array)
        {
            foreach (var buffer in buffers.EnumerateArray())
            {
                token.ThrowIfCancellationRequested();
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
                token.ThrowIfCancellationRequested();
                if (image.TryGetProperty("uri", out var uri) && uri.ValueKind == JsonValueKind.String)
                {
                    AddSafeUri(uri.GetString(), true, deps);
                }
            }
        }
    }

    private static void DiscoverObjDependencies(string objPath, string packageDir, Dictionary<string, string> deps, CancellationToken token)
    {
        if (new FileInfo(objPath).Length > 256L * 1024 * 1024)
            throw new UnsupportedDependencyReferenceException("OBJ document exceeds the discovery limit.");
        var mtlFiles = new HashSet<string>(StringComparer.Ordinal);
        foreach (var line in ReadPackageLines(objPath))
        {
            token.ThrowIfCancellationRequested();
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
            token.ThrowIfCancellationRequested();
            var mtlPath = RootPathRules.ResolveContainedPath(packageDir, packageDir, mtlName, nameof(mtlName));
            if (!File.Exists(mtlPath)) continue;
            CheckDocumentSize(mtlPath);
            var mtlDirectory = Path.GetDirectoryName(mtlName.Replace('\\', '/'))?.Replace('\\', '/');

            foreach (var line in ReadPackageLines(mtlPath))
            {
                token.ThrowIfCancellationRequested();
                var trimmed = line.Trim();
                if (trimmed.Length == 0 || trimmed.StartsWith("#")) continue;
                var tokens = trimmed.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
                if (tokens.Length < 2) continue;
                var directive = tokens[0].ToLowerInvariant();
                if (directive is "map_kd" or "map_bump" or "bump" or "map_d" or "map_ks" or "map_ka" or "map_ns" or "disp" or "decal")
                {
                    AddSafeUri(tokens[^1], false, deps, mtlDirectory);
                }
            }
        }
    }

    private static void DiscoverDaeDependencies(string daePath, Dictionary<string, string> deps, CancellationToken token)
    {
        CheckDocumentSize(daePath);
        using var stream = OpenPackageFile(daePath);
        using var reader = new StreamReader(stream);
        var text = reader.ReadToEndAsync(token).GetAwaiter().GetResult();
        var regex = new Regex(@"<init_from>\s*(.*?)\s*</init_from>", RegexOptions.IgnoreCase | RegexOptions.Singleline, TimeSpan.FromSeconds(2));
        foreach (Match match in regex.Matches(text))
        {
            token.ThrowIfCancellationRequested();
            var value = match.Groups[1].Value.Trim();
            if (!string.IsNullOrEmpty(value))
            {
                AddSafeUri(value, true, deps);
            }
        }
    }

    private static bool AddSafeUri(string? uri, bool unescape, Dictionary<string, string> deps, string? declaringDirectory = null)
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

        if (!string.IsNullOrEmpty(declaringDirectory)) candidate = declaringDirectory + "/" + candidate;
        var normalized = ModelPackagePlan.NormalizeComponentPath(candidate);
        if (string.IsNullOrEmpty(normalized))
        {
            throw new FormatException("Model dependency reference is empty after normalization.");
        }

        if (deps.Count >= MaxDependencies && !deps.ContainsKey(candidate))
            throw new UnsupportedDependencyReferenceException("Model dependency count exceeds the discovery limit.");
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
