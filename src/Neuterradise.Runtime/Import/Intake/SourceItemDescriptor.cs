using Neuterradise.App.Media;

namespace Neuterradise.App.Import.Intake;

/// <summary>
/// What the intake scan actually observed about one source file.
/// <see cref="LastWriteUtc"/> is null when the file does not exist or its timestamp could not be
/// read; a missing timestamp is never replaced with the current time, because that would record a
/// fabricated source fact.
/// </summary>
public sealed record SourceItemDescriptor(
    string FullSourcePath,
    bool Exists,
    bool IsReadable,
    long ByteLength,
    DateTimeOffset? LastWriteUtc,
    MediaType? Classification);
