namespace Neuterradise.App.Presentation;

public enum PresentationVisualPriority
{
    Surface,
    ActiveCard,
    VisibleCard,
    SelectedPreview,
    VisiblePreview,
    SettingsPreview,
}

public static class PresentationVisualPolicy
{
    public const int FullHostBudget = 6;
    public const int AnimatedPreviewBudget = 3;

    public static VisualTier Quality(PresentationVisualPriority priority, int rank, int previewRank) =>
        (int)priority >= (int)PresentationVisualPriority.SelectedPreview && previewRank >= AnimatedPreviewBudget
            ? VisualTier.Fallback : rank < FullHostBudget ? VisualTier.Full : VisualTier.Reduced;

    public static BackdropPlan PreserveEffectContinuity(BackdropPlan plan)
    {
        if (!plan.Variants.TryGetValue(VisualTier.Full, out var full)) return plan;
        if (!Visible(full)) return plan;
        var variants = plan.Variants.ToDictionary(pair => pair.Key, pair => pair.Value);
        if (!Visible(plan.For(VisualTier.Reduced)))
            variants[VisualTier.Reduced] = new BackdropVariant(full.Layers.Select(layer => layer with
            {
                Count = layer.Count == 0 ? 0 : Math.Max(1, layer.Count / 3),
                Speed = layer.Speed * 0.6,
                Depth = 0,
            }).ToArray());
        if (!Visible(plan.For(VisualTier.Fallback)))
            variants[VisualTier.Fallback] = new BackdropVariant(variants[VisualTier.Reduced].Layers, Static: true);
        return plan with { Variants = variants };
        static bool Visible(BackdropVariant variant) => variant.Layers.Any(layer => layer.Opacity > 0
            && (layer.Kind != "particles" || layer.Count > 0));
    }

    public static BackdropPlan PreserveBackdropContinuity(BackdropPlan plan)
    {
        if (!plan.Variants.TryGetValue(VisualTier.Full, out var full)) return plan;
        if (full.Layers.Count == 0) return plan;
        var variants = plan.Variants.ToDictionary(pair => pair.Key, pair => pair.Value);
        var still = full.Layers.Where(layer => layer.Kind is not "video" and not "lottie")
            .Select(layer => layer with { Amplitude = 0, Depth = 0 }).ToArray();
        if (still.Length == 0)
            still = [new BackdropLayer("scrim", Color: "token:canvas"),
                new BackdropLayer("radial-glow", Opacity: 0.2, Color: "token:accentSecondary")];
        foreach (var tier in new[] { VisualTier.Reduced, VisualTier.Fallback })
            if (!variants.TryGetValue(tier, out var variant) || !variant.Layers.Any(layer => layer.Opacity > 0 && layer.Kind is not "video" and not "lottie"))
                variants[tier] = new BackdropVariant(still, Static: true);
        return plan with { Variants = variants };
    }

    // Keep the calm wash perceptually separate from its canvas across light and dark palettes.
    public static ArgbColor CalmTint(ArgbColor canvas, ArgbColor tint)
    {
        var distance = Math.Max(Math.Abs(tint.R - canvas.R), Math.Max(Math.Abs(tint.G - canvas.G), Math.Abs(tint.B - canvas.B)));
        if (distance < 28)
        {
            var direction = (canvas.R + canvas.G + canvas.B) / 3 < 128 ? 1 : -1;
            return new(255, Shift(canvas.R), Shift(canvas.G), Shift(canvas.B));
            byte Shift(byte channel) => (byte)Math.Clamp(channel + direction * 28, 0, 255);
        }
        var strength = 28.0 / distance;
        return new(255, Mix(canvas.R, tint.R), Mix(canvas.G, tint.G), Mix(canvas.B, tint.B));
        byte Mix(byte channel, byte target) => (byte)Math.Clamp(Math.Round(channel + (target - channel) * strength), 0, 255);
    }
}
