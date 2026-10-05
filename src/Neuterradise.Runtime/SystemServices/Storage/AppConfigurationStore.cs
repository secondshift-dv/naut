using Neuterradise.App.Localization;
using System.IO;
using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Neuterradise.App.SystemServices.Storage;

/// <summary>
/// Persisted AppState window placement. Values are device-independent units (DIP).
/// </summary>
public sealed record WindowPlacementConfiguration(
    double Left,
    double Top,
    double Width,
    double Height,
    bool IsMaximized)
{
    public bool IsUsable =>
        Width > 0
        && Height > 0
        && !double.IsNaN(Left)
        && !double.IsNaN(Top)
        && !double.IsNaN(Width)
        && !double.IsNaN(Height)
        && !double.IsInfinity(Left)
        && !double.IsInfinity(Top)
        && !double.IsInfinity(Width)
        && !double.IsInfinity(Height);
}

/// <summary>
/// Persisted update preferences. The canonical stable GitHub Release feed is available by default,
/// while automatic startup checking remains disabled. Network access therefore occurs only after an
/// explicit update action unless a future user preference deliberately enables scheduled checking.
/// </summary>
public sealed record UpdateConfiguration(
    string? FeedUrl,
    bool AutomaticCheckEnabled,
    long? LastCheckedAtUnixMs)
{
    public const string DefaultFeedUrl =
        "https://github.com/secondshift-dv/naut/releases/latest/download/update.json";
    private const string LegacyCanonicalFeedUrl =
        "https://github.com/secondshift-dv/neuterradise/releases/latest/download/update.json";

    public static UpdateConfiguration CreateDefault() => new(DefaultFeedUrl, false, null);

    /// <summary>
    /// A feed is usable only when it is an absolute HTTPS URL. Anything else is treated as absent so
    /// the UI shows a configuration reason rather than a dead button.
    /// </summary>
    public bool HasConfiguredFeed =>
        !string.IsNullOrWhiteSpace(FeedUrl)
        && Uri.TryCreate(FeedUrl, UriKind.Absolute, out var uri)
        && uri.Scheme == Uri.UriSchemeHttps;

    public UpdateConfiguration Normalize()
    {
        // Schema-1 configurations may persist either no canonical feed or the pre-rename
        // secondshift-dv/neuterradise feed. Converge only that known legacy value to the current
        // repository; preserve any other valid custom HTTPS feed exactly as the user configured it.
        var feed = string.IsNullOrWhiteSpace(FeedUrl)
            ? DefaultFeedUrl
            : FeedUrl.Trim();
        if (string.Equals(feed, LegacyCanonicalFeedUrl, StringComparison.OrdinalIgnoreCase))
        {
            feed = DefaultFeedUrl;
        }
        var normalized = this with { FeedUrl = feed };
        return normalized.HasConfiguredFeed
            ? normalized
            : normalized with
            {
                FeedUrl = DefaultFeedUrl,
                AutomaticCheckEnabled = false,
            };
    }
}

/// <summary>
/// Schema 1 application configuration stored in AppStateRoot/config.json.
/// <c>VaultRoot</c> is null until the user completes onboarding; it is never defaulted to the
/// application folder or the working directory.
/// </summary>
public sealed record AppConfiguration(
    string? VaultRoot,
    string UiLanguage,
    string? LegacyThemeId,
    UpdateConfiguration Updates,
    WindowPlacementConfiguration? WindowPlacement)
{
    public const int CurrentSchemaVersion = 1;

    public static AppConfiguration CreateDefault() =>
        new(
            VaultRoot: null,
            UiLanguage: LanguageCatalog.DefaultCode,
            LegacyThemeId: null,
            Updates: UpdateConfiguration.CreateDefault(),
            WindowPlacement: null);

    public bool HasVaultRoot => !string.IsNullOrWhiteSpace(VaultRoot);
}

public enum AppConfigurationStatus
{
    /// <summary>Configuration file was read and understood.</summary>
    Loaded,

    /// <summary>No configuration file exists yet; onboarding must choose a Vault.</summary>
    Missing,

    /// <summary>The file exists but is not readable as schema 1; the original bytes are preserved.</summary>
    Unreadable,

    /// <summary>Windows denied access to the configuration file or its folder.</summary>
    AccessDenied,
}

/// <summary>
/// Result of a configuration load. A corrupt file never silently becomes defaults that would be
/// written back over the real settings; <see cref="MayOverwrite"/> gates that.
/// </summary>
public sealed record AppConfigurationLoadResult(
    AppConfigurationStatus Status,
    AppConfiguration Configuration,
    string ConfigurationFilePath,
    string? PreservedCopyPath,
    string? SafeErrorDetail)
{
    public bool IsLoaded => Status == AppConfigurationStatus.Loaded;

    /// <summary>
    /// True when saving is allowed to replace the on-disk file. An unreadable file may be replaced
    /// only after its bytes were preserved for diagnosis.
    /// </summary>
    public bool MayOverwrite =>
        Status == AppConfigurationStatus.Loaded
        || Status == AppConfigurationStatus.Missing
        || (Status == AppConfigurationStatus.Unreadable && PreservedCopyPath is not null);
}

public sealed record AppConfigurationSaveResult(
    bool IsSaved,
    string ConfigurationFilePath,
    string? SafeErrorDetail)
{
    public static AppConfigurationSaveResult Saved(string path) => new(true, path, null);

    public static AppConfigurationSaveResult Failed(string path, string detail) => new(false, path, detail);
}

/// <summary>
/// Single owner of AppStateRoot/config.json read/write. Writes go to a temporary sibling, are flushed
/// to disk, re-read for parse validity and then atomically replace the live file.
/// </summary>
public sealed class AppConfigurationStore
{
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> _mutationGates =
        new(StringComparer.OrdinalIgnoreCase);

    private static readonly JsonSerializerOptions _serializerOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
        WriteIndented = true,
        AllowTrailingCommas = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
    };

    private readonly AppStatePaths _appState;
    private readonly SemaphoreSlim _mutationGate;

    public AppConfigurationStore(AppStatePaths appState)
    {
        ArgumentNullException.ThrowIfNull(appState);

        _appState = appState;
        _mutationGate = _mutationGates.GetOrAdd(
            Path.GetFullPath(appState.ConfigurationFilePath),
            static _ => new SemaphoreSlim(1, 1));
    }

    public string ConfigurationFilePath => _appState.ConfigurationFilePath;

    public AppConfiguration CreateDefaultConfiguration()
        => AppConfiguration.CreateDefault();

    public async Task<AppConfigurationLoadResult> LoadAsync(CancellationToken cancellationToken = default)
    {
        var path = ConfigurationFilePath;
        var defaults = CreateDefaultConfiguration();

        byte[] bytes;
        try
        {
            if (!File.Exists(path))
            {
                return new AppConfigurationLoadResult(
                    AppConfigurationStatus.Missing,
                    defaults,
                    path,
                    PreservedCopyPath: null,
                    SafeErrorDetail: null);
            }

            bytes = await File.ReadAllBytesAsync(path, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (UnauthorizedAccessException exception)
        {
            return new AppConfigurationLoadResult(
                AppConfigurationStatus.AccessDenied,
                defaults,
                path,
                PreservedCopyPath: null,
                SafeErrorDetail: exception.Message);
        }
        catch (IOException exception)
        {
            return new AppConfigurationLoadResult(
                AppConfigurationStatus.AccessDenied,
                defaults,
                path,
                PreservedCopyPath: null,
                SafeErrorDetail: exception.Message);
        }

        try
        {
            var document = JsonSerializer.Deserialize<ConfigurationDocument>(bytes, _serializerOptions)
                ?? throw new JsonException("The configuration document is null.");

            if (document.SchemaVersion != AppConfiguration.CurrentSchemaVersion)
            {
                throw new JsonException(
                    $"Unsupported configuration schemaVersion {document.SchemaVersion}.");
            }

            return new AppConfigurationLoadResult(
                AppConfigurationStatus.Loaded,
                Normalize(document.ToConfiguration(defaults)),
                path,
                PreservedCopyPath: null,
                SafeErrorDetail: null);
        }
        catch (Exception exception) when (exception is JsonException or NotSupportedException or ArgumentException)
        {
            var preserved = PreserveUnreadableFile(path);
            return new AppConfigurationLoadResult(
                AppConfigurationStatus.Unreadable,
                defaults,
                path,
                preserved,
                exception.Message);
        }
    }

    public async Task<AppConfigurationSaveResult> SaveAsync(
        AppConfiguration configuration,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        await _mutationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await SaveCoreAsync(configuration, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _mutationGate.Release();
        }
    }

    /// <summary>
    /// Serializes a read-modify-write against the latest durable configuration. The mutation runs
    /// under the configuration-file gate and must contain no unrelated UI or I/O work.
    /// </summary>
    public async Task<AppConfigurationSaveResult> UpdateAsync(
        Func<AppConfiguration, AppConfiguration> mutation,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(mutation);

        await _mutationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var load = await LoadAsync(cancellationToken).ConfigureAwait(false);
            if (!load.MayOverwrite)
            {
                return AppConfigurationSaveResult.Failed(
                    load.ConfigurationFilePath,
                    load.SafeErrorDetail ?? "The existing configuration cannot be safely replaced.");
            }

            var updated = mutation(load.Configuration)
                ?? throw new InvalidOperationException("The configuration mutation returned null.");
            return updated == load.Configuration
                ? AppConfigurationSaveResult.Saved(load.ConfigurationFilePath)
                : await SaveCoreAsync(updated, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _mutationGate.Release();
        }
    }

    private async Task<AppConfigurationSaveResult> SaveCoreAsync(
        AppConfiguration configuration,
        CancellationToken cancellationToken)
    {

        var path = ConfigurationFilePath;
        var normalized = Normalize(configuration);
        var tempPath = $"{path}.tmp-{Guid.NewGuid():N}";
        var backupPath = $"{path}.bak-{Guid.NewGuid():N}";

        try
        {
            _appState.EnsureStructuralDirectories();

            var payload = JsonSerializer.SerializeToUtf8Bytes(
                ConfigurationDocument.FromConfiguration(normalized),
                _serializerOptions);

            await using (var stream = new FileStream(
                tempPath,
                FileMode.Create,
                FileAccess.Write,
                FileShare.None,
                bufferSize: 4096,
                FileOptions.WriteThrough))
            {
                await stream.WriteAsync(payload, cancellationToken).ConfigureAwait(false);
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
                stream.Flush(flushToDisk: true);
            }

            // Prove the durable bytes parse before they become the live configuration.
            var writtenBytes = await File.ReadAllBytesAsync(tempPath, cancellationToken).ConfigureAwait(false);
            _ = JsonSerializer.Deserialize<ConfigurationDocument>(writtenBytes, _serializerOptions)
                ?? throw new JsonException("The staged configuration could not be re-read.");

            if (File.Exists(path))
            {
                try
                {
                    File.Replace(tempPath, path, backupPath, ignoreMetadataErrors: true);
                }
                catch (PlatformNotSupportedException)
                {
                    File.Move(tempPath, path, overwrite: true);
                }
            }
            else
            {
                File.Move(tempPath, path);
            }

            return AppConfigurationSaveResult.Saved(path);
        }
        catch (OperationCanceledException)
        {
            TryDelete(tempPath);
            throw;
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or JsonException or NotSupportedException)
        {
            TryDelete(tempPath);
            return AppConfigurationSaveResult.Failed(path, exception.Message);
        }
        finally
        {
            TryDelete(backupPath);
        }
    }

    private AppConfiguration Normalize(AppConfiguration configuration)
    {
        var vaultRoot = configuration.VaultRoot;
        if (string.IsNullOrWhiteSpace(vaultRoot))
        {
            vaultRoot = null;
        }
        else
        {
            try
            {
                vaultRoot = RootPathRules.NormalizeRoot(vaultRoot, nameof(configuration.VaultRoot));
            }
            catch (ArgumentException)
            {
                // A relative or malformed persisted Vault root is treated as unselected rather than
                // being resolved against the working directory.
                vaultRoot = null;
            }
        }

        var language = LanguageCatalog.NormalizeOrDefault(configuration.UiLanguage);

        var placement = configuration.WindowPlacement is { IsUsable: true }
            ? configuration.WindowPlacement
            : null;

        var updates = configuration.Updates is { } configuredUpdates
            ? configuredUpdates.Normalize()
            : UpdateConfiguration.CreateDefault();

        return configuration with
        {
            VaultRoot = vaultRoot,
            UiLanguage = language,
            Updates = updates,
            WindowPlacement = placement,
        };
    }

    /// <summary>
    /// Copies the unreadable configuration next to the original so the user keeps evidence of what
    /// was lost. The original is left in place; the caller decides whether to replace it.
    /// </summary>
    private static string? PreserveUnreadableFile(string path)
    {
        var preservedPath = $"{path}.unreadable-{DateTime.UtcNow:yyyyMMddHHmmssfff}";
        try
        {
            File.Copy(path, preservedPath, overwrite: false);
            return preservedPath;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private sealed record ConfigurationDocument
    {
        public int SchemaVersion { get; init; } = AppConfiguration.CurrentSchemaVersion;

        public string? VaultRoot { get; init; }

        public string? UiLanguage { get; init; }

        [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
        public string? ThemeId { get; init; }

        public UpdateDocument? Updates { get; init; }

        public WindowPlacementDocument? WindowPlacement { get; init; }

        public static ConfigurationDocument FromConfiguration(AppConfiguration configuration) => new()
        {
            SchemaVersion = AppConfiguration.CurrentSchemaVersion,
            VaultRoot = configuration.VaultRoot,
            UiLanguage = configuration.UiLanguage,
            ThemeId = configuration.LegacyThemeId,
            Updates = new UpdateDocument
            {
                FeedUrl = configuration.Updates.FeedUrl,
                AutomaticCheckEnabled = configuration.Updates.AutomaticCheckEnabled,
                LastCheckedAtUnixMs = configuration.Updates.LastCheckedAtUnixMs,
            },
            WindowPlacement = configuration.WindowPlacement is null
                ? null
                : new WindowPlacementDocument
                {
                    Left = configuration.WindowPlacement.Left,
                    Top = configuration.WindowPlacement.Top,
                    Width = configuration.WindowPlacement.Width,
                    Height = configuration.WindowPlacement.Height,
                    IsMaximized = configuration.WindowPlacement.IsMaximized,
                },
        };

        public AppConfiguration ToConfiguration(AppConfiguration defaults) => new(
            VaultRoot: VaultRoot,
            UiLanguage: UiLanguage ?? defaults.UiLanguage,
            LegacyThemeId: ThemeId,
            Updates: Updates is null
                ? UpdateConfiguration.CreateDefault()
                : new UpdateConfiguration(
                    Updates.FeedUrl,
                    Updates.AutomaticCheckEnabled ?? false,
                    Updates.LastCheckedAtUnixMs),
            WindowPlacement: WindowPlacement is null
                ? null
                : new WindowPlacementConfiguration(
                    WindowPlacement.Left ?? 0,
                    WindowPlacement.Top ?? 0,
                    WindowPlacement.Width ?? 0,
                    WindowPlacement.Height ?? 0,
                    WindowPlacement.IsMaximized ?? false));
    }

    private sealed record UpdateDocument
    {
        public string? FeedUrl { get; init; }

        public bool? AutomaticCheckEnabled { get; init; }

        public long? LastCheckedAtUnixMs { get; init; }
    }

    private sealed record WindowPlacementDocument
    {
        public double? Left { get; init; }

        public double? Top { get; init; }

        public double? Width { get; init; }

        public double? Height { get; init; }

        public bool? IsMaximized { get; init; }
    }
}
