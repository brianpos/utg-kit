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
    private ResourceIndex? _index;

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

    public CodeSystemIndexEntry? GetCodeSystemIndexEntry(string id)
        => EnsureIndex().CodeSystems.GetValueOrDefault(id);

    public CodeSystem? LoadCodeSystem(string id)
        => LoadResource<CodeSystem>(GetCodeSystemIndexEntry(id));

    #endregion

    #region ValueSet

    public IReadOnlyList<ValueSetIndexEntry> GetValueSetIndex()
        => EnsureIndex().ValueSets.Values
            .OrderBy(e => e.Title ?? e.Name ?? e.Id)
            .ToList();

    public ValueSetIndexEntry? GetValueSetIndexEntry(string id)
        => EnsureIndex().ValueSets.GetValueOrDefault(id);

    public ValueSet? LoadValueSet(string id)
        => LoadResource<ValueSet>(GetValueSetIndexEntry(id));

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

        return entries.OrderByDescending(e => e.Date).ToList();
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

    private ResourceIndex BuildIndex()
    {
        var codeSystems = new Dictionary<string, CodeSystemIndexEntry>(StringComparer.OrdinalIgnoreCase);
        var valueSets = new Dictionary<string, ValueSetIndexEntry>(StringComparer.OrdinalIgnoreCase);
        var conceptMaps = new Dictionary<string, ConceptMapIndexEntry>(StringComparer.OrdinalIgnoreCase);
        var bundles = new Dictionary<string, BundleIndexEntry>(StringComparer.OrdinalIgnoreCase);
        var lists = new Dictionary<string, ListIndexEntry>(StringComparer.OrdinalIgnoreCase);
        var provenances = new Dictionary<string, ProvenanceIndexEntry>(StringComparer.OrdinalIgnoreCase);
        var history = new Dictionary<string, List<HistoryEntry>>(StringComparer.OrdinalIgnoreCase);

        var files = FindAllFiles("*.xml").Concat(FindAllFiles("*.json"));

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
                    valueSets[vs.Id] = new ValueSetIndexEntry(
                        file, vs.Id, vs.Url, vs.Version, vs.Name, vs.Title,
                        vs.Status, vs.Description, GetOwner(vs), vs.Date);
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
        }

        return new ResourceIndex(codeSystems, valueSets, conceptMaps, bundles, lists, provenances, history);
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
        Dictionary<string, List<HistoryEntry>> History);
}
