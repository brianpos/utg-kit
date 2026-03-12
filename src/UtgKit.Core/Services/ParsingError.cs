namespace UtgKit.Core.Services;

/// <summary>
/// Records a parsing failure encountered when reading a FHIR resource file.
/// </summary>
public sealed record ParsingError(
    string FilePath,
    string ErrorMessage,
    DateTimeOffset DetectedAt);
