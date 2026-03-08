using Hl7.Fhir.Model;
using FhirList = Hl7.Fhir.Model.List;

namespace UtgKit.Core.Services;

/// <summary>
/// Base index entry shared by all FHIR resource types in the index.
/// </summary>
public abstract record ResourceIndexEntry(
    string FilePath,
    string Id,
    string? Url,
    string? Version,
    string? Name,
    string? Title,
    PublicationStatus? Status,
    string? Description,
    string? Owner,
    string? Date);

/// <summary>
/// Lightweight in-memory index entry for a CodeSystem.
/// </summary>
public sealed record CodeSystemIndexEntry(
    string FilePath,
    string Id,
    string? Url,
    string? Version,
    string? Name,
    string? Title,
    PublicationStatus? Status,
    string? Description,
    string? Owner,
    string? Date,
    CodeSystemContentMode? Content,
    int ConceptCount)
    : ResourceIndexEntry(FilePath, Id, Url, Version, Name, Title, Status, Description, Owner, Date);

/// <summary>
/// Lightweight in-memory index entry for a ValueSet.
/// </summary>
public sealed record ValueSetIndexEntry(
    string FilePath,
    string Id,
    string? Url,
    string? Version,
    string? Name,
    string? Title,
    PublicationStatus? Status,
    string? Description,
    string? Owner,
    string? Date)
    : ResourceIndexEntry(FilePath, Id, Url, Version, Name, Title, Status, Description, Owner, Date);

/// <summary>
/// Lightweight in-memory index entry for a ConceptMap.
/// </summary>
public sealed record ConceptMapIndexEntry(
    string FilePath,
    string Id,
    string? Url,
    string? Version,
    string? Name,
    string? Title,
    PublicationStatus? Status,
    string? Description,
    string? Owner,
    string? Date,
    string? SourceUri,
    string? TargetUri)
    : ResourceIndexEntry(FilePath, Id, Url, Version, Name, Title, Status, Description, Owner, Date);

/// <summary>
/// Lightweight in-memory index entry for a Bundle.
/// </summary>
public sealed record BundleIndexEntry(
    string FilePath,
    string Id,
    Bundle.BundleType? Type,
    int EntryCount)
    : ResourceIndexEntry(FilePath, Id, null, null, null, null, null, null, null, null);

/// <summary>
/// Lightweight in-memory index entry for a List resource.
/// </summary>
public sealed record ListIndexEntry(
    string FilePath,
    string Id,
    string? Title,
    FhirList.ListStatus? ListStatus,
    ListMode? Mode,
    int EntryCount)
    : ResourceIndexEntry(FilePath, Id, null, null, null, Title, null, null, null, null);

/// <summary>
/// Lightweight in-memory index entry for a Provenance resource.
/// </summary>
public sealed record ProvenanceIndexEntry(
    string FilePath,
    string Id,
    string? Recorded,
    int TargetCount)
    : ResourceIndexEntry(FilePath, Id, null, null, null, null, null, null, null, null);
