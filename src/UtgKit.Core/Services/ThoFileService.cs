using Hl7.Fhir.Model;
using Hl7.Fhir.Serialization;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using FhirList = Hl7.Fhir.Model.List;

namespace UtgKit.Core.Services;

/// <summary>
/// Reads FHIR resources from the local THO repository clone.
/// Maintains an in-memory index of resource metadata for fast lookups.
/// </summary>
public class ThoFileService
{
    private readonly ThoRepoSettings _settings;
    private readonly FhirXmlParser _xmlParser = new();
    private readonly FhirJsonParser _jsonParser = new();
    private readonly FhirXmlSerializer _xmlSerializer = new(new SerializerSettings { Pretty = true });
    private readonly FhirJsonSerializer _jsonSerializer = new(new SerializerSettings { Pretty = true });
    private readonly ILogger<ThoFileService> _logger;

    private readonly object _indexLock = new();
    private readonly SemaphoreSlim _asyncIndexLock = new(1, 1);
    private ResourceIndex? _index;

    /// <summary>
    /// Raised during index building with the latest progress.
    /// Fired from a background thread — subscribers must marshal to their own context.
    /// </summary>
    public event Action<IndexingProgress>? IndexingProgressChanged;

    /// <summary>
    /// Raised after the index is incrementally updated (file added, changed, or removed).
    /// Subscribers should marshal to their own context.
    /// </summary>
    public event Action? IndexChanged;

    /// <summary>
    /// The most recent indexing progress, or null if indexing has not started.
    /// </summary>
    public IndexingProgress? CurrentIndexingProgress { get; private set; }

    public ThoFileService(IOptions<ThoRepoSettings> settings, ILogger<ThoFileService> logger)
    {
        _settings = settings.Value;
        _logger = logger;
    }

    #region CodeSystem

    public IReadOnlyList<CodeSystemIndexEntry> GetCodeSystemIndex()
        => EnsureIndex().CodeSystems.Values
            .OrderBy(e => e.Title ?? e.Name ?? e.Id)
            .ToList();

    /// <summary>
    /// Returns the CodeSystem index, reporting progress during initial build.
    /// </summary>
    public async System.Threading.Tasks.Task<IReadOnlyList<CodeSystemIndexEntry>> GetCodeSystemIndexAsync()
        => (await EnsureIndexAsync()).CodeSystems.Values
            .OrderBy(e => e.Title ?? e.Name ?? e.Id)
            .ToList();

    public CodeSystemIndexEntry? GetCodeSystemIndexEntry(string id)
        => EnsureIndex().CodeSystems.GetValueOrDefault(id);

    public CodeSystem? LoadCodeSystem(string id)
        => LoadResource<CodeSystem>(GetCodeSystemIndexEntry(id));

    public CodeSystemIndexEntry? GetCodeSystemIndexEntryByUrl(string canonicalUrl)
        => EnsureIndex().CodeSystems.Values
            .FirstOrDefault(e => string.Equals(e.Url, canonicalUrl, StringComparison.OrdinalIgnoreCase));

    public CodeSystem? LoadCodeSystemByUrl(string canonicalUrl)
        => LoadResource<CodeSystem>(GetCodeSystemIndexEntryByUrl(canonicalUrl));

    #endregion

    #region ValueSet

    public IReadOnlyList<ValueSetIndexEntry> GetValueSetIndex()
        => EnsureIndex().ValueSets.Values
            .OrderBy(e => e.Title ?? e.Name ?? e.Id)
            .ToList();

    /// <summary>
    /// Returns the ValueSet index, reporting progress during initial build.
    /// </summary>
    public async System.Threading.Tasks.Task<IReadOnlyList<ValueSetIndexEntry>> GetValueSetIndexAsync()
        => (await EnsureIndexAsync()).ValueSets.Values
            .OrderBy(e => e.Title ?? e.Name ?? e.Id)
            .ToList();

    public ValueSetIndexEntry? GetValueSetIndexEntry(string id)
        => EnsureIndex().ValueSets.GetValueOrDefault(id);

    public ValueSet? LoadValueSet(string id)
        => LoadResource<ValueSet>(GetValueSetIndexEntry(id));

    public ValueSet? LoadValueSetByUrl(string canonicalUrl)
    {
        var entry = EnsureIndex().ValueSets.Values
            .FirstOrDefault(e => string.Equals(e.Url, canonicalUrl, StringComparison.OrdinalIgnoreCase));
        return LoadResource<ValueSet>(entry);
    }

    /// <summary>
    /// Returns index entries for all ValueSets whose compose includes the given CodeSystem URL.
    /// </summary>
    public IReadOnlyList<ValueSetIndexEntry> GetValueSetsReferencingCodeSystem(string codeSystemUrl)
        => EnsureIndex().ValueSets.Values
            .Where(e => e.ReferencedCodeSystemUrls
                .Any(u => string.Equals(u, codeSystemUrl, StringComparison.OrdinalIgnoreCase)))
            .OrderBy(e => e.Title ?? e.Name ?? e.Id)
            .ToList();

    #endregion

    #region ConceptMap

    public IReadOnlyList<ConceptMapIndexEntry> GetConceptMapIndex()
        => EnsureIndex().ConceptMaps.Values
            .OrderBy(e => e.Title ?? e.Name ?? e.Id)
            .ToList();

    public ConceptMapIndexEntry? GetConceptMapIndexEntry(string id)
        => EnsureIndex().ConceptMaps.GetValueOrDefault(id);

    public ConceptMap? LoadConceptMap(string id)
        => LoadResource<ConceptMap>(GetConceptMapIndexEntry(id));

    #endregion

    #region Bundle

    public IReadOnlyList<BundleIndexEntry> GetBundleIndex()
        => EnsureIndex().Bundles.Values
            .OrderBy(e => e.Id)
            .ToList();

    public BundleIndexEntry? GetBundleIndexEntry(string id)
        => EnsureIndex().Bundles.GetValueOrDefault(id);

    public Bundle? LoadBundle(string id)
        => LoadResource<Bundle>(GetBundleIndexEntry(id));

    #endregion

    #region List

    public IReadOnlyList<ListIndexEntry> GetListIndex()
        => EnsureIndex().Lists.Values
            .OrderBy(e => e.Title ?? e.Id)
            .ToList();

    public ListIndexEntry? GetListIndexEntry(string id)
        => EnsureIndex().Lists.GetValueOrDefault(id);

    public FhirList? LoadList(string id)
        => LoadResource<FhirList>(GetListIndexEntry(id));

    #endregion

    #region Manifest Groups

    /// <summary>
    /// Returns all rendering manifest group names (e.g. "fhir", "v2", "v3").
    /// </summary>
    public IReadOnlyList<string> GetManifestGroups()
        => EnsureIndex().ManifestGroups.Keys.OrderBy(k => k, StringComparer.OrdinalIgnoreCase).ToList();

    /// <summary>
    /// Returns the set of manifest group names that contain the given resource reference
    /// (e.g. "CodeSystem/action-type" → ["fhir"]).
    /// </summary>
    public IReadOnlyList<string> GetManifestGroupsForResource(string resourceReference)
        => EnsureIndex().ManifestGroupMembers.TryGetValue(resourceReference, out var groups)
            ? groups.OrderBy(g => g, StringComparer.OrdinalIgnoreCase).ToList()
            : [];

    /// <summary>
    /// Returns the set of resource references belonging to a manifest group.
    /// </summary>
    public IReadOnlySet<string> GetManifestGroupReferences(string groupName)
        => EnsureIndex().ManifestGroups.TryGetValue(groupName, out var refs)
            ? refs
            : new HashSet<string>(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Adds a resource reference to a rendering manifest group and persists the change.
    /// </summary>
    public void AddResourceToManifestGroup(string groupName, string resourceReference, string resourceType)
    {
        var manifestPath = GetRenderingManifestPath(groupName);
        if (manifestPath is null)
        {
            _logger.LogWarning("No rendering manifest found for group {Group}", groupName);
            return;
        }

        try
        {
            var content = File.ReadAllText(manifestPath);
            var list = _xmlParser.Parse<FhirList>(content);

            var alreadyExists = list.Entry?.Any(e =>
                e.Item?.Reference == resourceReference) == true;

            if (!alreadyExists)
            {
                list.Entry ??= [];
                list.Entry.Add(new FhirList.EntryComponent
                {
                    Item = new ResourceReference
                    {
                        Reference = resourceReference,
                        Type = resourceType
                    }
                });

                var xml = _xmlSerializer.SerializeToString(list);
                File.WriteAllText(manifestPath, "<?xml version=\"1.0\" encoding=\"UTF-8\"?>\n" + xml);
                _logger.LogInformation("Added {Reference} to manifest group {Group}", resourceReference, groupName);

                // Update the in-memory index
                var index = EnsureIndex();
                if (!index.ManifestGroups.TryGetValue(groupName, out var refs))
                {
                    refs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    index.ManifestGroups[groupName] = refs;
                }
                refs.Add(resourceReference);

                if (!index.ManifestGroupMembers.TryGetValue(resourceReference, out var groups))
                {
                    groups = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    index.ManifestGroupMembers[resourceReference] = groups;
                }
                groups.Add(groupName);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to add {Reference} to manifest group {Group}", resourceReference, groupName);
        }
    }

    /// <summary>
    /// Removes a resource reference from a rendering manifest group and persists the change.
    /// </summary>
    public void RemoveResourceFromManifestGroup(string groupName, string resourceReference)
    {
        var manifestPath = GetRenderingManifestPath(groupName);
        if (manifestPath is null)
        {
            _logger.LogWarning("No rendering manifest found for group {Group}", groupName);
            return;
        }

        try
        {
            var content = File.ReadAllText(manifestPath);
            var list = _xmlParser.Parse<FhirList>(content);

            var removed = list.Entry?.RemoveAll(e =>
                string.Equals(e.Item?.Reference, resourceReference, StringComparison.OrdinalIgnoreCase)) ?? 0;

            if (removed > 0)
            {
                var xml = _xmlSerializer.SerializeToString(list);
                File.WriteAllText(manifestPath, "<?xml version=\"1.0\" encoding=\"UTF-8\"?>\n" + xml);
                _logger.LogInformation("Removed {Reference} from manifest group {Group}", resourceReference, groupName);

                // Update the in-memory index
                var index = EnsureIndex();
                if (index.ManifestGroups.TryGetValue(groupName, out var refs))
                    refs.Remove(resourceReference);

                if (index.ManifestGroupMembers.TryGetValue(resourceReference, out var groups))
                    groups.Remove(groupName);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to remove {Reference} from manifest group {Group}", resourceReference, groupName);
        }
    }

    #endregion

    #region Provenance

    public IReadOnlyList<ProvenanceIndexEntry> GetProvenanceIndex()
        => EnsureIndex().Provenances.Values
            .OrderBy(e => e.Id)
            .ToList();

    public ProvenanceIndexEntry? GetProvenanceIndexEntry(string id)
        => EnsureIndex().Provenances.GetValueOrDefault(id);

    public Provenance? LoadProvenance(string id)
        => LoadResource<Provenance>(GetProvenanceIndexEntry(id));

    #endregion

    #region History

    /// <summary>
    /// Returns history entries for a resource, sorted by date descending.
    /// The <paramref name="resourceReference"/> should be in the form "ResourceType/id",
    /// e.g. "CodeSystem/action-type".
    /// </summary>
    public IReadOnlyList<HistoryEntry> GetResourceHistory(string resourceReference)
    {
        var index = EnsureIndex();
        if (!index.History.TryGetValue(resourceReference, out var entries))
            return [];

        // Entries are stored in file order (earliest index first).
        // Reverse so that later-in-file entries come first, then stable-sort
        // by date descending - this keeps "last in file = top" for same dates.
        return entries
            .AsEnumerable()
            .Reverse()
            .OrderByDescending(e => e.Date)
            .ToList();
    }

    /// <summary>
    /// Appends a Provenance entry to a history bundle for the given resource reference.
    /// If <paramref name="historyFile"/> is null or empty, a new bundle is created.
    /// After appending, the in-memory history index is updated and <see cref="IndexChanged"/> is raised.
    /// </summary>
    public void AppendProvenanceEntry(
        string resourceReference,
        string activityCode,
        string changeComment,
        string? historyFile,
        string? authorName,
        string? custodianName)
    {
        if (string.IsNullOrWhiteSpace(_settings.Path))
            throw new InvalidOperationException("THO repo path is not configured.");

        string historyPath;

        if (!string.IsNullOrEmpty(historyFile))
        {
            historyPath = Path.Combine(_settings.Path, "history", historyFile);
            if (!File.Exists(historyPath))
            {
                _logger.LogWarning("History file not found: {Path}", historyPath);
                return;
            }
        }
        else
        {
            var historyDir = Path.Combine(_settings.Path, "history");
            Directory.CreateDirectory(historyDir);

            var timestamp = DateTime.UtcNow.ToString("yyyyMMdd-HHmmss");
            var newFileName = $"utgrel-history-{timestamp}.json";
            historyPath = Path.Combine(historyDir, newFileName);

            var newBundle = new Bundle
            {
                Id = $"hx-history-{timestamp}",
                Type = Bundle.BundleType.Collection,
                Entry = []
            };

            var json = _jsonSerializer.SerializeToString(newBundle);
            File.WriteAllText(historyPath, json);
            _logger.LogInformation("Created new history bundle: {Path}", historyPath);
        }

        try
        {
            var content = File.ReadAllText(historyPath);
            var bundle = _jsonParser.Parse<Bundle>(content);

            var now = DateTimeOffset.UtcNow;
            // Extract resource id from reference (e.g. "CodeSystem/action-type" -> "action-type")
            var resourceId = resourceReference.Contains('/')
                ? resourceReference[(resourceReference.IndexOf('/') + 1)..]
                : resourceReference;
            var provenanceId = $"hx-{resourceId}-{now:yyyyMMdd}";

            var provenance = new Provenance
            {
                Id = provenanceId,
                Target = [new ResourceReference(resourceReference)],
                Occurred = new Period
                {
                    EndElement = new FhirDateTime(now)
                },
                Recorded = now,
                Authorization =
                [
                    new CodeableReference
                    {
                        Concept = new CodeableConcept
                        {
                            Coding =
                            [
                                new Coding
                                {
                                    System = "http://terminology.hl7.org/CodeSystem/v3-ActReason",
                                    Code = "METAMGT"
                                }
                            ],
                            Text = changeComment
                        }
                    }
                ],
                Activity = new CodeableConcept
                {
                    Coding =
                    [
                        new Coding
                        {
                            System = "http://terminology.hl7.org/CodeSystem/v3-DataOperation",
                            Code = activityCode
                        }
                    ]
                },
                Agent =
                [
                    new Provenance.AgentComponent
                    {
                        Type = new CodeableConcept
                        {
                            Coding =
                            [
                                new Coding
                                {
                                    System = "http://terminology.hl7.org/CodeSystem/provenance-participant-type",
                                    Code = "author"
                                }
                            ]
                        },
                        Who = new ResourceReference { Display = authorName ?? "Unknown" }
                    },
                    new Provenance.AgentComponent
                    {
                        Type = new CodeableConcept
                        {
                            Coding =
                            [
                                new Coding
                                {
                                    System = "http://terminology.hl7.org/CodeSystem/provenance-participant-type",
                                    Code = "custodian"
                                }
                            ]
                        },
                        Who = new ResourceReference { Display = custodianName ?? "TSMG" }
                    }
                ]
            };

            bundle.Entry ??= [];
            bundle.Entry.Add(new Bundle.EntryComponent
            {
                FullUrl = $"http://terminology.hl7.org/fhir/Provenance/{provenanceId}",
                Resource = provenance
            });

            var updatedJson = _jsonSerializer.SerializeToString(bundle);
            File.WriteAllText(historyPath, updatedJson);
            _logger.LogInformation("Appended provenance {Id} to history bundle {Path}", provenanceId, historyPath);

            // Update the in-memory index with only the new entry (not the whole bundle,
            // which would re-add already-indexed entries as duplicates).
            var index = EnsureIndex();
            var singleEntryBundle = new Bundle
            {
                Id = bundle.Id,
                Entry = [bundle.Entry[^1]]
            };
            ExtractHistoryFromBundle(singleEntryBundle, index.History);
            IndexChanged?.Invoke();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to update history bundle at {Path}", historyPath);
            throw;
        }
    }

    #endregion

    /// <summary>
    /// Forces the entire index to be rebuilt on the next access.
    /// Call this after files are added, removed, or modified on disk.
    /// </summary>
    public void InvalidateIndex()
    {
        lock (_indexLock)
        {
            _index = null;
        }
    }

    #region File-Watcher Suppression

    private int _suppressionCount;

    /// <summary>
    /// Returns true when file-watcher processing should be suppressed
    /// (e.g. because an import operation is writing files).
    /// </summary>
    public bool IsWatcherSuppressed => Volatile.Read(ref _suppressionCount) > 0;

    /// <summary>
    /// Suppresses file-watcher processing for the lifetime of the returned token.
    /// Dispose the token to re-enable watching. Calls nest safely.
    /// </summary>
    public IDisposable SuppressWatcher()
    {
        Interlocked.Increment(ref _suppressionCount);
        return new SuppressionToken(this);
    }

    private sealed class SuppressionToken(ThoFileService owner) : IDisposable
    {
        private int _disposed;
        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
                Interlocked.Decrement(ref owner._suppressionCount);
        }
    }

    #endregion

    #region Incremental Index Updates

    /// <summary>
    /// Re-parses a single file and upserts its entry in the in-memory index.
    /// If the file no longer exists or cannot be parsed, the previous entry (if any) is removed.
    /// Returns true if the index was modified.
    /// </summary>
    public bool ReindexFile(string filePath)
    {
        lock (_indexLock)
        {
            if (_index is null)
                return false; // index not built yet; nothing to update

            // Remove any previous entry for this file path first
            var removed = RemoveFromIndexByPath(_index, filePath);

            if (!File.Exists(filePath))
            {
                if (removed)
                {
                    _logger.LogInformation("Removed deleted file from index: {Path}", filePath);
                    IndexChanged?.Invoke();
                }
                return removed;
            }

            var resource = ParseResource<Resource>(filePath);
            if (resource?.Id is null)
            {
                if (removed)
                {
                    _logger.LogInformation("Removed unparseable file from index: {Path}", filePath);
                    IndexChanged?.Invoke();
                }
                return removed;
            }

            var added = AddToIndex(_index, filePath, resource);
            if (added || removed)
            {
                _logger.LogInformation("Reindexed file: {Path} ({Type}/{Id})",
                    filePath, resource.TypeName, resource.Id);
                IndexChanged?.Invoke();
            }
            return added || removed;
        }
    }

    /// <summary>
    /// Removes all index entries that reference the given file path.
    /// Returns true if the index was modified.
    /// </summary>
    public bool RemoveFileFromIndex(string filePath)
    {
        lock (_indexLock)
        {
            if (_index is null)
                return false;

            var removed = RemoveFromIndexByPath(_index, filePath);
            if (removed)
            {
                _logger.LogInformation("Removed file from index: {Path}", filePath);
                IndexChanged?.Invoke();
            }
            return removed;
        }
    }

    private bool AddToIndex(ResourceIndex index, string filePath, Resource resource)
    {
        switch (resource)
        {
            case CodeSystem cs:
                index.CodeSystems[cs.Id] = new CodeSystemIndexEntry(
                    filePath, cs.Id, cs.Url, cs.Version, cs.Name, cs.Title,
                    cs.Status, cs.Description, GetOwner(cs), cs.Date,
                    cs.Content, CountConcepts(cs.Concept));
                return true;

            case ValueSet vs:
                var referencedSystems = vs.Compose?.Include?
                    .Select(i => i.System)
                    .Where(s => !string.IsNullOrEmpty(s))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList() ?? [];
                index.ValueSets[vs.Id] = new ValueSetIndexEntry(
                    filePath, vs.Id, vs.Url, vs.Version, vs.Name, vs.Title,
                    vs.Status, vs.Description, GetOwner(vs), vs.Date,
                    referencedSystems!);
                return true;

            case ConceptMap cm:
                index.ConceptMaps[cm.Id] = new ConceptMapIndexEntry(
                    filePath, cm.Id, cm.Url, cm.Version, cm.Name, cm.Title,
                    cm.Status, cm.Description, GetOwner(cm), cm.Date,
                    (cm.SourceScope as FhirUri)?.Value ?? (cm.SourceScope as Canonical)?.Value,
                    (cm.TargetScope as FhirUri)?.Value ?? (cm.TargetScope as Canonical)?.Value);
                return true;

            case Bundle b:
                index.Bundles[b.Id] = new BundleIndexEntry(
                    filePath, b.Id, b.Type, b.Entry?.Count ?? 0);
                ExtractHistoryFromBundle(b, index.History);
                return true;

            case FhirList l:
                index.Lists[l.Id] = new ListIndexEntry(
                    filePath, l.Id, l.Title, l.Status, l.Mode, l.Entry?.Count ?? 0);
                return true;

            case Provenance p:
                index.Provenances[p.Id] = new ProvenanceIndexEntry(
                    filePath, p.Id, p.Recorded?.ToString("o"), p.Target?.Count ?? 0);
                return true;

            default:
                return false;
        }
    }

    private static bool RemoveFromIndexByPath(ResourceIndex index, string filePath)
    {
        var removed = false;
        removed |= RemoveByPath(index.CodeSystems, filePath);
        removed |= RemoveByPath(index.ValueSets, filePath);
        removed |= RemoveByPath(index.ConceptMaps, filePath);

        // When removing a Bundle, also clear its history entries so that
        // a subsequent re-index (via AddToIndex → ExtractHistoryFromBundle)
        // doesn't duplicate them.
        var bundleKey = index.Bundles
            .FirstOrDefault(kv => string.Equals(kv.Value.FilePath, filePath, StringComparison.OrdinalIgnoreCase)).Key;
        if (bundleKey is not null)
        {
            index.Bundles.Remove(bundleKey);
            foreach (var list in index.History.Values)
                list.RemoveAll(h => string.Equals(h.BundleId, bundleKey, StringComparison.OrdinalIgnoreCase));
            removed = true;
        }

        removed |= RemoveByPath(index.Lists, filePath);
        removed |= RemoveByPath(index.Provenances, filePath);
        return removed;
    }

    private static bool RemoveByPath<T>(Dictionary<string, T> dict, string filePath) where T : ResourceIndexEntry
    {
        var key = dict.FirstOrDefault(kv => string.Equals(kv.Value.FilePath, filePath, StringComparison.OrdinalIgnoreCase)).Key;
        if (key is not null)
        {
            dict.Remove(key);
            return true;
        }
        return false;
    }

    #endregion

    /// <summary>
    /// Returns the top-level content subdirectory names within the THO repository
    /// (e.g. "fhir", "v2"), excluding infrastructure directories like "history"
    /// and "control-manifests".
    /// </summary>
    public IReadOnlyList<string> GetContentSubdirectories()
    {
        if (string.IsNullOrWhiteSpace(_settings.Path))
            return [];

        var root = new DirectoryInfo(_settings.Path);
        if (!root.Exists)
            return [];

        var excluded = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "history",
            "control-manifests"
        };

        return root.EnumerateDirectories()
            .Where(d => !excluded.Contains(d.Name))
            .Select(d => d.Name)
            .OrderBy(n => n, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>
    /// Returns the file names of history bundles in the history directory,
    /// sorted so the highest version number appears first.
    /// </summary>
    public IReadOnlyList<string> GetHistoryFiles()
    {
        if (string.IsNullOrWhiteSpace(_settings.Path))
            return [];

        var historyDir = new DirectoryInfo(Path.Combine(_settings.Path, "history"));
        if (!historyDir.Exists)
            return [];

        return historyDir.EnumerateFiles("*.json")
            .Select(f => f.Name)
            .OrderByDescending(n => n, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>
    /// Returns the full path to the rendering manifest for a content subdirectory
    /// (e.g. "fhir" → control-manifests/fhir-Rendering.xml), or null if not found.
    /// </summary>
    public string? GetRenderingManifestPath(string contentSubdirectory)
    {
        if (string.IsNullOrWhiteSpace(_settings.Path))
            return null;

        var manifestDir = Path.Combine(_settings.Path, "control-manifests");
        var pattern = $"{contentSubdirectory}-Rendering.xml";

        // Case-insensitive search
        var dir = new DirectoryInfo(manifestDir);
        if (!dir.Exists)
            return null;

        var match = dir.EnumerateFiles("*-Rendering.xml")
            .FirstOrDefault(f => f.Name.Equals(pattern, StringComparison.OrdinalIgnoreCase));

        return match?.FullName;
    }

    /// <summary>
    /// Serializes a FHIR resource to pretty-printed XML.
    /// </summary>
    public string SerializeToXml(Resource resource) => _xmlSerializer.SerializeToString(resource);

    /// <summary>
    /// Serializes a FHIR resource to pretty-printed JSON.
    /// </summary>
    public string SerializeToJson(Resource resource) => _jsonSerializer.SerializeToString(resource);

    private T? LoadResource<T>(ResourceIndexEntry? entry) where T : Resource
    {
        if (entry is null)
            return null;

        return ParseResource<T>(entry.FilePath);
    }

    private ResourceIndex EnsureIndex()
    {
        lock (_indexLock)
        {
            if (_index is not null)
                return _index;

            _index = BuildIndex();
            _logger.LogInformation(
                "Built resource index: {CS} CodeSystems, {VS} ValueSets, {CM} ConceptMaps, " +
                "{B} Bundles, {L} Lists, {P} Provenances",
                _index.CodeSystems.Count, _index.ValueSets.Count, _index.ConceptMaps.Count,
                _index.Bundles.Count, _index.Lists.Count, _index.Provenances.Count);
            return _index;
        }
    }

    /// <summary>
    /// Ensures the resource index is built, reporting progress during the initial build.
    /// Returns immediately if the index is already cached.
    /// </summary>
    private async System.Threading.Tasks.Task<ResourceIndex> EnsureIndexAsync()
    {
        // Fast path: index already built
        if (_index is not null)
            return _index;

        await _asyncIndexLock.WaitAsync();
        try
        {
            // Double-check after acquiring lock
            if (_index is not null)
                return _index;

            _index = await System.Threading.Tasks.Task.Run(BuildIndex);
            _logger.LogInformation(
                "Built resource index: {CS} CodeSystems, {VS} ValueSets, {CM} ConceptMaps, " +
                "{B} Bundles, {L} Lists, {P} Provenances",
                _index.CodeSystems.Count, _index.ValueSets.Count, _index.ConceptMaps.Count,
                _index.Bundles.Count, _index.Lists.Count, _index.Provenances.Count);
            return _index;
        }
        finally
        {
            _asyncIndexLock.Release();
        }
    }

    private ResourceIndex BuildIndex()
    {
        var codeSystems = new Dictionary<string, CodeSystemIndexEntry>(StringComparer.OrdinalIgnoreCase);
        var valueSets = new Dictionary<string, ValueSetIndexEntry>(StringComparer.OrdinalIgnoreCase);
        var conceptMaps = new Dictionary<string, ConceptMapIndexEntry>(StringComparer.OrdinalIgnoreCase);
        var bundles = new Dictionary<string, BundleIndexEntry>(StringComparer.OrdinalIgnoreCase);
        var lists = new Dictionary<string, ListIndexEntry>(StringComparer.OrdinalIgnoreCase);
        var provenances = new Dictionary<string, ProvenanceIndexEntry>(StringComparer.OrdinalIgnoreCase);
        var history = new Dictionary<string, List<HistoryEntry>>(StringComparer.OrdinalIgnoreCase);

        var files = FindAllFiles("*.xml").Concat(FindAllFiles("*.json")).ToList();
        var totalFiles = files.Count;
        var processedFiles = 0;

        ReportProgress(new IndexingProgress(totalFiles, 0));

        foreach (var file in files)
        {
            var resource = ParseResource<Resource>(file);
            if (resource?.Id is null)
                continue;

            switch (resource)
            {
                case CodeSystem cs:
                    codeSystems[cs.Id] = new CodeSystemIndexEntry(
                        file, cs.Id, cs.Url, cs.Version, cs.Name, cs.Title,
                        cs.Status, cs.Description, GetOwner(cs), cs.Date,
                        cs.Content, CountConcepts(cs.Concept));
                    break;

                case ValueSet vs:
                    var referencedSystems = vs.Compose?.Include?
                        .Select(i => i.System)
                        .Where(s => !string.IsNullOrEmpty(s))
                        .Distinct(StringComparer.OrdinalIgnoreCase)
                        .ToList() ?? [];
                    valueSets[vs.Id] = new ValueSetIndexEntry(
                        file, vs.Id, vs.Url, vs.Version, vs.Name, vs.Title,
                        vs.Status, vs.Description, GetOwner(vs), vs.Date,
                        referencedSystems!);
                    break;

                case ConceptMap cm:
                    conceptMaps[cm.Id] = new ConceptMapIndexEntry(
                        file, cm.Id, cm.Url, cm.Version, cm.Name, cm.Title,
                        cm.Status, cm.Description, GetOwner(cm), cm.Date,
                        (cm.SourceScope as FhirUri)?.Value ?? (cm.SourceScope as Canonical)?.Value,
                        (cm.TargetScope as FhirUri)?.Value ?? (cm.TargetScope as Canonical)?.Value);
                    break;

                case Bundle b:
                    bundles[b.Id] = new BundleIndexEntry(
                        file, b.Id, b.Type, b.Entry?.Count ?? 0);
                    ExtractHistoryFromBundle(b, history);
                    break;

                case FhirList l:
                    lists[l.Id] = new ListIndexEntry(
                        file, l.Id, l.Title, l.Status, l.Mode, l.Entry?.Count ?? 0);
                    break;

                case Provenance p:
                    provenances[p.Id] = new ProvenanceIndexEntry(
                        file, p.Id, p.Recorded?.ToString("o"), p.Target?.Count ?? 0);
                    break;
            }

            processedFiles++;
            if (processedFiles % 50 == 0 || processedFiles == totalFiles)
                ReportProgress(new IndexingProgress(totalFiles, processedFiles));
        }

        // Build manifest group index from rendering manifests
        var manifestGroups = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
        var manifestGroupMembers = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
        BuildManifestGroupIndex(manifestGroups, manifestGroupMembers);

        return new ResourceIndex(codeSystems, valueSets, conceptMaps, bundles, lists, provenances, history, manifestGroups, manifestGroupMembers);
    }

    private void BuildManifestGroupIndex(
        Dictionary<string, HashSet<string>> manifestGroups,
        Dictionary<string, HashSet<string>> manifestGroupMembers)
    {
        if (string.IsNullOrWhiteSpace(_settings.Path))
            return;

        var manifestDir = new DirectoryInfo(Path.Combine(_settings.Path, "control-manifests"));
        if (!manifestDir.Exists)
            return;

        foreach (var file in manifestDir.EnumerateFiles("*-Rendering.xml"))
        {
            // Extract group name: "fhir-Rendering.xml" → "fhir"
            var groupName = file.Name[..file.Name.IndexOf("-Rendering", StringComparison.OrdinalIgnoreCase)];
            var refs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            try
            {
                var content = File.ReadAllText(file.FullName);
                var list = _xmlParser.Parse<FhirList>(content);

                if (list.Entry is not null)
                {
                    foreach (var entry in list.Entry)
                    {
                        var reference = entry.Item?.Reference;
                        if (string.IsNullOrEmpty(reference))
                            continue;

                        refs.Add(reference);

                        if (!manifestGroupMembers.TryGetValue(reference, out var groups))
                        {
                            groups = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                            manifestGroupMembers[reference] = groups;
                        }
                        groups.Add(groupName);
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Failed to parse rendering manifest: {Path}", file.FullName);
            }

            manifestGroups[groupName] = refs;
        }
    }

    private void ReportProgress(IndexingProgress progress)
    {
        CurrentIndexingProgress = progress;
        IndexingProgressChanged?.Invoke(progress);
    }

    private static void ExtractHistoryFromBundle(Bundle bundle, Dictionary<string, List<HistoryEntry>> history)
    {
        if (bundle.Entry is null)
            return;

        foreach (var entry in bundle.Entry)
        {
            if (entry.Resource is not Provenance prov)
                continue;

            var dateStr = prov.Recorded?.ToString("yyyy-MM-dd");
            var activityCode = prov.Activity?.Coding?.FirstOrDefault()?.Code;
            var activityDisplay = prov.Activity?.Coding?.FirstOrDefault()?.Display;
            var authorAgent = prov.Agent?.FirstOrDefault(a =>
                a.Type?.Coding?.Any(c => c.Code == "author") == true) ?? prov.Agent?.FirstOrDefault();
            var custodianAgent = prov.Agent?.FirstOrDefault(a =>
                a.Type?.Coding?.Any(c => c.Code == "custodian") == true);

            var author = authorAgent?.Who?.Display;
            var authorizingGroup = custodianAgent?.Who?.Display
                ?? authorAgent?.OnBehalfOf?.Display;
            var detail = prov.Authorization?.FirstOrDefault()?.Concept?.Text;

            var historyEntry = new HistoryEntry(
                bundle.Id,
                dateStr ?? "",
                activityCode,
                activityDisplay,
                author,
                authorizingGroup,
                detail);

            if (prov.Target is null)
                continue;

            foreach (var target in prov.Target)
            {
                if (string.IsNullOrEmpty(target.Reference))
                    continue;

                if (!history.TryGetValue(target.Reference, out var list))
                {
                    list = [];
                    history[target.Reference] = list;
                }

                list.Add(historyEntry);
            }
        }
    }

    private static int CountConcepts(List<CodeSystem.ConceptDefinitionComponent>? concepts)
    {
        if (concepts is null or { Count: 0 })
            return 0;

        var count = 0;
        foreach (var concept in concepts)
        {
            count++;
            count += CountConcepts(concept.Concept);
        }

        return count;
    }

    private const string WgExtensionUrl = "http://hl7.org/fhir/StructureDefinition/structuredefinition-wg";

    private static string? GetOwner(DomainResource resource)
    {
        return resource.Extension
            ?.FirstOrDefault(e => e.Url == WgExtensionUrl)
            ?.Value is Code code ? code.Value : null;
    }

    private T? ParseResource<T>(string path) where T : Resource
    {
        try
        {
            var content = File.ReadAllText(path);
            return path.EndsWith(".json", StringComparison.OrdinalIgnoreCase)
                ? _jsonParser.Parse<T>(content)
                : _xmlParser.Parse<T>(content);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Skipped file during indexing: {Path}", path);
            return null;
        }
    }

    private IEnumerable<string> FindAllFiles(string pattern)
    {
        if (string.IsNullOrWhiteSpace(_settings.Path))
            return [];

        var root = new DirectoryInfo(_settings.Path);
        if (!root.Exists)
        {
            _logger.LogWarning("THO source path does not exist: {Path}", _settings.Path);
            return [];
        }

        return root.EnumerateFiles(pattern, SearchOption.AllDirectories)
                   .Select(f => f.FullName);
    }

    private sealed record ResourceIndex(
        Dictionary<string, CodeSystemIndexEntry> CodeSystems,
        Dictionary<string, ValueSetIndexEntry> ValueSets,
        Dictionary<string, ConceptMapIndexEntry> ConceptMaps,
        Dictionary<string, BundleIndexEntry> Bundles,
        Dictionary<string, ListIndexEntry> Lists,
        Dictionary<string, ProvenanceIndexEntry> Provenances,
        Dictionary<string, List<HistoryEntry>> History,
        Dictionary<string, HashSet<string>> ManifestGroups,
        Dictionary<string, HashSet<string>> ManifestGroupMembers);
}
