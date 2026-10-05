using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Neuterradise.App.Import;
using Neuterradise.App.Import.Intake;
using Neuterradise.App.Presentation;
using Neuterradise.App.Profiles;
using Neuterradise.App.Shell;
using Neuterradise.App.SystemServices.Database;
using Neuterradise.App.SystemServices.Lifecycle;
using Neuterradise.App.SystemServices.Storage;

namespace Neuterradise.App.Ui;

/// <summary>The composition root handed to every surface. Services, never UI objects, flow down from here.</summary>
public sealed class AppServices
{
    public required CatalogDb Catalog { get; init; }

    public required VaultPaths Paths { get; init; }

    public required ProductionRuntimeRegistry Runtime { get; init; }

    public required ProfileRuntimeSnapshotCache ProfileSnapshots { get; init; }

    public required PresentationRuntime Presentation { get; init; }

    public required NavigationCoordinator Navigation { get; init; }

    public required RouteViewModelFactory Factory { get; init; }

    public required OverlayHostViewModel Overlay { get; init; }

    public required ImportActivityService ImportActivity { get; init; }

    public required ImportFinalizer Finalizer { get; init; }

    public required ShellStatusViewModel Status { get; init; }

    public required AppConfigurationStore Configuration { get; init; }

    public required Window Window { get; init; }

    public FigureRuntime? Figures { get; init; }

    public required Func<string, Task<VaultTransitionCommandResult>> RequestVaultRenameAsync { get; init; }

    public required Func<string, Task<VaultTransitionCommandResult>> RequestVaultMoveAsync { get; init; }

    public required Func<string, Task<VaultTransitionCommandResult>> RequestVaultChangeAsync { get; init; }

    public ProductRoot? Root { get; set; }

    /// <summary>Raised on the UI thread after a presentation Apply or pack change.</summary>
    public event Action<PresentationChangedEventArgs>? PresentationChanged;

    internal void RaisePresentationChanged(PresentationChangedEventArgs args) => PresentationChanged?.Invoke(args);

    public void OpenCustomization(string category, PresentationContext? context = null, string? slot = null, object? subject = null) =>
        Root?.ShowCustomization(category, context ?? PresentationContext.Global, slot, subject);

    public void Toast(string message, string tone = "info") => Root?.ShowToast(message, tone);

    public Task ImportAsync(
        IEnumerable<string> paths,
        Guid? destinationProfileId,
        IntakeOrigin origin = IntakeOrigin.Picker) =>
        Root?.ImportAsync(paths, destinationProfileId, origin) ?? Task.CompletedTask;

    public Task<bool> ConfirmAsync(string title, string message, string confirm, bool destructive = false, string? cancel = null) =>
        Root?.Dialogs.ConfirmAsync(title, message, confirm, destructive, cancel) ?? Task.FromResult(false);

    public Task<IReadOnlyList<string>> PickFilesAsync(bool multiple, params string[] extensions) => Pickers.PickFilesAsync(Window, multiple, extensions);

    public Task<string?> PickFolderAsync() => Pickers.PickFolderAsync(Window);

    public Task<string?> PickSaveFileAsync(string suggestedName, string extension, string label) => Pickers.PickSaveFileAsync(Window, suggestedName, extension, label);

    public void RunUserAction(Task task, string context, string failureMessage) =>
        TaskObserver.Observe(task, context, _ => Toast(failureMessage, "error"));
}

public interface ITopBarCustomizableSurface
{
    string TopBarCustomizeLabel { get; }

    bool CanTopBarCustomize { get; }

    void OpenTopBarCustomization();
}

public abstract class Surface : IDisposable
{
    private bool _disposed;
    protected PreviewSession? Preview { get; private set; }

    protected Surface(AppServices services) => Services = services;

    public AppServices Services { get; }

    public abstract FrameworkElement View { get; }

    public virtual ScreenStateViewModel? Model => null;

    public bool IsActive { get; private set; }

    public AppRoute? LastRoute { get; private set; }

    protected Disposables Bag { get; } = new();

    public void SetPreviewSession(PreviewSession? session)
    {
        if (Preview is not null) Preview.Changed -= OnPreviewChanged;
        Preview = session;
        if (Preview is not null) Preview.Changed += OnPreviewChanged;
        PresentationEffects.SetPreviewScope(View, () => Preview);
        RefreshPreviewPresentation();
        PresentationEffects.RefreshTree(View);
    }

    private void OnPreviewChanged(object? sender, string slot)
    {
        UiDispatch.Run(() =>
        {
            if (_disposed || !ReferenceEquals(sender, Preview)) return;
            RefreshPreviewPresentation(slot);
            if (slot.StartsWith("effect.", StringComparison.Ordinal)) PresentationEffects.RefreshTree(View, slot);
        });
    }

    protected PresentationContext PreviewContext(PresentationContext context) =>
        Preview?.ProfileWorking is { } profile && context.ProfileId == profile.ProfileId
            ? context with { Profile = profile } : context;

    protected CompiledDefinition ResolvePresentation(string slot, PresentationContext context) =>
        Services.Presentation.ResolveCompiled(slot, PreviewContext(context), Preview?.Working);

    protected virtual void RefreshPreviewPresentation(string? slot = null) { }

    public void Activate(AppRoute route)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        IsActive = true;
        LastRoute = route;
        PresentationEffects.SetActivityScope(View, () => IsActive);
        OnActivated(route);
        PresentationEffects.RefreshTree(View);
    }

    public void Suspend()
    {
        if (_disposed)
        {
            return;
        }

        IsActive = false;
        OnSuspended();
        PresentationEffects.RefreshTree(View);
    }

    protected virtual void OnActivated(AppRoute route)
    {
    }

    protected virtual void OnSuspended()
    {
    }

    public virtual void OnPresentationChanged(PresentationChangedEventArgs args)
    {
    }

    public virtual void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        if (Preview is not null) Preview.Changed -= OnPreviewChanged;
        Preview = null;
        _disposed = true;
        IsActive = false;
        Bag.Dispose();
        var model = Model;
        model?.RetireRoute();
        if (model is IDisposable disposableModel)
        {
            disposableModel.Dispose();
        }
    }
}

public static class PresentationBinder
{
    public static void ApplyGlobal(PresentationRuntime presentation, BindingSet? bindings = null)
    {
        var context = PresentationContext.Global;
        var theme = presentation.ResolvePlan<ThemePlan>(PresentationSlots.Theme, context, bindings);
        ThemeRuntime.Current.Apply(theme,
            presentation.ResolvePlan<TypographyPlan>(PresentationSlots.Typography, context, bindings),
            presentation.ResolvePlan<ControlSkinPlan>(PresentationSlots.ControlSkin, context, bindings));
    }
}
