namespace UtgKit.Core.Services;

/// <summary>
/// A single history entry for a FHIR resource, extracted from a Provenance
/// resource inside a history Bundle.
/// </summary>
public sealed record HistoryEntry(
    string BundleId,
    string Date,
    string? ActivityCode,
    string? ActivityDisplay,
    string? Author,
    string? AuthorizingGroup,
    string? Detail);
