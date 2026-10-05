namespace Neuterradise.App.Import.Intake;

public enum IntakeOrigin
{
    Picker,
    DragDrop,
    ProfileAddMedia
}

public sealed record ImportIntakeRequest(
    IReadOnlyList<string> SourceEntries,
    IntakeOrigin Origin,
    Guid? SuggestedDestinationProfileId = null);
