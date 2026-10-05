namespace Neuterradise.App.Media;

/// <summary>Single current-media selection for gallery/profile media surfaces.</summary>
public sealed class MediaSelectionModel
{
    private Guid? _selectedMediaId;

    public event EventHandler? Changed;

    public Guid? SelectedMediaId => _selectedMediaId;

    public bool IsSelected(Guid assetId) =>
        assetId != Guid.Empty && _selectedMediaId == assetId;

    public void SelectOnly(Guid assetId)
    {
        RequireNonEmpty(assetId);
        if (_selectedMediaId == assetId)
        {
            return;
        }

        _selectedMediaId = assetId;
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public void ReconcileTo(IReadOnlyCollection<Guid> currentContextMediaIds)
    {
        ArgumentNullException.ThrowIfNull(currentContextMediaIds);
        if (_selectedMediaId is not { } selected || currentContextMediaIds.Contains(selected))
        {
            return;
        }

        _selectedMediaId = null;
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public void Clear()
    {
        if (_selectedMediaId is null)
        {
            return;
        }

        _selectedMediaId = null;
        Changed?.Invoke(this, EventArgs.Empty);
    }

    private static void RequireNonEmpty(Guid assetId)
    {
        if (assetId == Guid.Empty)
        {
            throw new ArgumentException("An Media id cannot be empty.", nameof(assetId));
        }
    }
}
