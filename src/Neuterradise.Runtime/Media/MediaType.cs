namespace Neuterradise.App.Media;

/// <summary>
/// Canonical persisted media kind: <c>IMAGE | VIDEO | MODEL</c>. The value is derived
/// from admitted content, never from user wording, and is mapped only through <c>DbEnum</c>.
/// </summary>
public enum MediaType
{
    Image,
    Video,
    Model,
}
