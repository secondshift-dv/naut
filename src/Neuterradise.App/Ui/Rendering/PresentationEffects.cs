using Microsoft.UI.Xaml;
using System.Runtime.CompilerServices;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.Foundation;
using Neuterradise.App.Presentation;

namespace Neuterradise.App.Ui;

public static class PresentationEffects
{
    private sealed class ContextScope(Func<PresentationContext> provider)
    {
        public Func<PresentationContext> Provider { get; } = provider;
    }

    private static readonly ConditionalWeakTable<FrameworkElement, ContextScope> ContextScopes = new();
    private sealed class PreviewScope(Func<PreviewSession?> provider)
    {
        public Func<PreviewSession?> Provider { get; } = provider;
    }
    private static readonly ConditionalWeakTable<FrameworkElement, PreviewScope> PreviewScopes = new();
    private sealed class ActivityScope(Func<bool> provider)
    {
        public Func<bool> Provider { get; } = provider;
    }
    private static readonly ConditionalWeakTable<FrameworkElement, ActivityScope> ActivityScopes = new();
    private static PresentationRuntime? _runtime;

    public static PresentationRuntime? Runtime
    {
        get => _runtime;
        set
        {
            if (ReferenceEquals(_runtime, value)) return;
            if (_runtime is not null) _runtime.Changed -= OnRuntimeChanged;
            _runtime = value;
            if (_runtime is not null) _runtime.Changed += OnRuntimeChanged;
        }
    }

    public static EffectView View(string slot, Func<PresentationContext> context, FrameworkElement? interactionOwner = null,
        Func<EffectPlan?>? preview = null) => new(slot, context, interactionOwner, preview);

    public static EffectView Preview(EffectPlan plan, double height,
        PresentationVisualPriority priority = PresentationVisualPriority.SettingsPreview)
    {
        var target = new[] { PresentationSlots.HomeEffect, PresentationSlots.ProfileEffect, PresentationSlots.CardEffect }
            .FirstOrDefault(plan.Supports) ?? PresentationSlots.HomeEffect;
        return new EffectView(target, () => PresentationContext.Global, null, () => plan) { Height = height, VisualPriority = priority };
    }

    public static void SetContextScope(FrameworkElement element, Func<PresentationContext> provider)
    {
        ArgumentNullException.ThrowIfNull(element);
        ArgumentNullException.ThrowIfNull(provider);
        ContextScopes.Remove(element);
        ContextScopes.Add(element, new ContextScope(provider));
    }

    public static void SetPreviewScope(FrameworkElement element, Func<PreviewSession?> provider)
    {
        PreviewScopes.Remove(element);
        PreviewScopes.Add(element, new PreviewScope(provider));
    }

    public static void SetActivityScope(FrameworkElement element, Func<bool> provider)
    {
        ActivityScopes.Remove(element);
        ActivityScopes.Add(element, new ActivityScope(provider));
    }

    internal static bool IsTreeActive(FrameworkElement element)
    {
        for (DependencyObject? current = element; current is not null; current = VisualTreeHelper.GetParent(current))
            if (current is FrameworkElement framework && ActivityScopes.TryGetValue(framework, out var scope) && !scope.Provider()) return false;
        return true;
    }

    internal static EffectPlan ResolvePlan(FrameworkElement element, string slot, PresentationContext context)
    {
        for (DependencyObject? current = element; current is not null; current = VisualTreeHelper.GetParent(current))
        {
            if (current is FrameworkElement framework && PreviewScopes.TryGetValue(framework, out var scope)
                && scope.Provider() is { } session)
            {
                if (session.ProfileWorking is { } profile && context.ProfileId == profile.ProfileId)
                    context = context with { Profile = profile };
                return Runtime!.ResolvePlan<EffectPlan>(slot, context, session.Working);
            }
        }
        return Runtime!.ResolvePlan<EffectPlan>(slot, context);
    }

    public static void RefreshTree(DependencyObject element, string? slot = null)
    {
        if (element is EffectView effect && (slot is null || effect.Slot == slot)) effect.Refresh();
        if (element is BackdropView backdrop && slot is null) backdrop.RefreshActivity();
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(element); i++)
            RefreshTree(VisualTreeHelper.GetChild(element, i), slot);
    }

    internal static PresentationContext ResolveContext(FrameworkElement element, Func<PresentationContext> fallback)
    {
        for (DependencyObject? current = element; current is not null; current = VisualTreeHelper.GetParent(current))
        {
            if (current is FrameworkElement framework && ContextScopes.TryGetValue(framework, out var scope))
            {
                var scoped = scope.Provider();
                var supplied = fallback();
                return supplied.ProfileId is { } id && scoped.ProfileId != id
                    ? supplied : scoped;
            }
        }
        return fallback();
    }

    public static void Prewarm(PresentationRuntime runtime)
    {
        // Warm only the curated procedural families; no pack media decoding or unbounded library work.
        var curated = new HashSet<string>(StringComparer.Ordinal)
        {
            "builtin.neuterradise.effect.none", "builtin.neuterradise.effect.bubbles",
            "builtin.neuterradise.effect.rain", "builtin.neuterradise.effect.snow", "builtin.neuterradise.effect.silk",
        };
        var plans = runtime.DefinitionsOf(DefinitionKinds.Effect)
            .Where(definition => curated.Contains(definition.Ref.DefinitionId))
            .Select(definition => definition.PlanAs<EffectPlan>().Environment)
            .ToArray();
        BackdropView.PrewarmEffects(plans);
    }

    private static void OnRuntimeChanged(object? sender, PresentationChangedEventArgs args)
    {
        if (args.PacksChanged && _runtime is { } runtime)
            UiDispatch.Run(() => Prewarm(runtime));
    }
}

public sealed class EffectView : Grid
{
    private readonly string _slot;
    private readonly Func<PresentationContext> _context;
    private readonly BackdropView _view = new();
    private readonly FrameworkElement? _interactionOwner;
    private bool _hovered;
    private bool _focused;
    private ScrollViewer? _viewport;
    private bool _hasLayers;
    private bool _interactionOnly;
    private readonly Func<EffectPlan?>? _preview;
    private PresentationVisualPriority _visualPriority;
    public string Slot => _slot;
    public PresentationVisualPriority VisualPriority
    {
        get => _visualPriority;
        set { _visualPriority = value; _view.VisualPriority = value; }
    }
    public void SetPreviewSelected(bool selected) => VisualPriority = selected
        ? PresentationVisualPriority.SelectedPreview : PresentationVisualPriority.VisiblePreview;

    public EffectView(string slot, Func<PresentationContext> context, FrameworkElement? interactionOwner, Func<EffectPlan?>? preview = null)
    {
        _slot = slot;
        _context = context;
        _interactionOwner = interactionOwner;
        _preview = preview;
        VisualPriority = preview is not null ? PresentationVisualPriority.SelectedPreview
            : slot == PresentationSlots.CardEffect ? PresentationVisualPriority.VisibleCard : PresentationVisualPriority.Surface;
        IsHitTestVisible = false;
        SizeChanged += (_, _) => RefreshActivity();
        Loaded += (_, _) =>
        {
            if (PresentationEffects.Runtime is { } runtime) runtime.Changed += OnChanged;
            if (_interactionOwner is { } owner)
            {
                owner.PointerEntered += OnEntered;
                owner.PointerExited += OnExited;
                owner.GotFocus += OnFocus;
                owner.LostFocus += OnFocus;
            }
            Refresh();
        };
        Unloaded += (_, _) =>
        {
            if (PresentationEffects.Runtime is { } runtime) runtime.Changed -= OnChanged;
            if (_interactionOwner is { } owner)
            {
                owner.PointerEntered -= OnEntered;
                owner.PointerExited -= OnExited;
                owner.GotFocus -= OnFocus;
                owner.LostFocus -= OnFocus;
            }
            _view.IsSurfaceActive = false;
            if (_viewport is not null) _viewport.ViewChanged -= OnViewportChanged;
            _viewport = null;
            _hovered = _focused = false;
            Release();
        };
    }

    private void OnChanged(object? sender, PresentationChangedEventArgs args)
    {
        if (args.Affects(_slot)) UiDispatch.Run(Refresh);
    }
    private void OnViewportChanged(object? sender, ScrollViewerViewChangedEventArgs args) => RefreshActivity();

    private bool InViewport()
    {
        if (!IsLoaded || ActualWidth <= 0 || ActualHeight <= 0 || !PresentationEffects.IsTreeActive(this)) return false;
        for (DependencyObject? current = this; current is FrameworkElement framework; current = VisualTreeHelper.GetParent(current))
        {
            if (framework.Visibility != Visibility.Visible || framework.Opacity <= 0) return false;
        }
        if (_viewport is null) return true;
        try
        {
            var bounds = TransformToVisual(_viewport).TransformBounds(new Rect(0, 0, ActualWidth, ActualHeight));
            return bounds.Bottom > 0 && bounds.Right > 0 && bounds.X < _viewport.ActualWidth && bounds.Y < _viewport.ActualHeight;
        }
        catch (InvalidOperationException) { return false; }
    }
    private void OnEntered(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs args) { _hovered = true; RefreshActivity(); }
    private void OnExited(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs args) { _hovered = false; RefreshActivity(); }
    private void OnFocus(object sender, RoutedEventArgs args)
    {
        _focused = _interactionOwner is Control { FocusState: not FocusState.Unfocused };
        RefreshActivity();
    }

    public void Refresh()
    {
        if (PresentationEffects.Runtime is null) return;
        var plan = _preview?.Invoke() ?? PresentationEffects.ResolvePlan(this, _slot, PresentationEffects.ResolveContext(this, _context));
        _hasLayers = plan.Environment.Variants.Values.Any(variant => variant.Layers.Count != 0);
        if (!_hasLayers)
        {
            Release();
            _view.IsSurfaceActive = false;
            Children.Clear();
            if (_viewport is not null) _viewport.ViewChanged -= OnViewportChanged;
            _viewport = null;
            Opacity = 0;
            return;
        }
        if (_viewport is null && IsLoaded)
        {
            for (var parent = VisualTreeHelper.GetParent(this); parent is not null; parent = VisualTreeHelper.GetParent(parent))
                if (parent is ScrollViewer scroll)
                {
                    _viewport = scroll;
                    _viewport.ViewChanged += OnViewportChanged;
                    break;
                }
        }
        _interactionOnly = plan.InteractionOnly;
        _view.Plan = plan.Environment;
        if (Children.Count == 0) Children.Add(_view);
        RefreshActivity();
    }

    private void RefreshActivity()
    {
        if (!_hasLayers) return;
        var active = InViewport() && (!_interactionOnly || _hovered || _focused);
        _view.VisualPriority = _slot == PresentationSlots.CardEffect && _preview is null && (_hovered || _focused)
            ? PresentationVisualPriority.ActiveCard : VisualPriority;
        _view.IsSurfaceActive = active;
        Opacity = _interactionOnly && !active ? 0 : 1;
    }
    private void Release() => _view.IsSurfaceActive = false;
}
