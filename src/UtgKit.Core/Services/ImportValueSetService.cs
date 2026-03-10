using Hl7.Fhir.Model;
using Hl7.Fhir.Serialization;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace UtgKit.Core.Services;

/// <summary>
/// Result of a ValueSet import operation.
/// </summary>
public sealed record ImportValueSetResult(
    bool Success,
    string? ValueSetId,
    string? SavedFilePath,
    string? ErrorMessage,
    IReadOnlyList<ImportCodeSystemResult> CodeSystemResults);

/// <summary>
/// Downloads a ValueSet from the FHIR core build, saves it into the local
/// THO repository, updates the rendering manifest, and appends a provenance
/// entry to the selected history bundle. Optionally imports referenced
/// CodeSystem(s) as well.
/// </summary>
public class ImportValueSetService
{
    private readonly ThoRepoSettings _settings;
    private readonly ThoFileService _thoFileService;
    private readonly ImportCodeSystemService _importCodeSystemService;
    private readonly HttpClient _httpClient;
    private readonly FhirJsonParser _jsonParser = new();
    private readonly FhirJsonSerializer _jsonSerializer = new(new SerializerSettings { Pretty = true });
    private readonly FhirXmlSerializer _xmlSerializer = new(new SerializerSettings { Pretty = true });
    private readonly ILogger<ImportValueSetService> _logger;

    public ImportValueSetService(
        IOptions<ThoRepoSettings> settings,
        ThoFileService thoFileService,
        ImportCodeSystemService importCodeSystemService,
        HttpClient httpClient,
        ILogger<ImportValueSetService> logger)
    {
        _settings = settings.Value;
        _thoFileService = thoFileService;
        _importCodeSystemService = importCodeSystemService;
        _httpClient = httpClient;
        _logger = logger;
    }

    /// <summary>
    /// Normalizes a FHIR build URL to its JSON variant.
    /// Accepts .html, .xml, or .json URLs and returns the .json form.
    /// Validates the path contains "valueset" to confirm the resource type.
    /// </summary>
    public static (string? JsonUrl, string? Error) NormalizeUrl(string url)
    {
        if (!Uri.TryCreate(url.Trim(), UriKind.Absolute, out var uri))
            return (null, "Invalid URL.");

        var path = uri.AbsolutePath;
        var filename = Path.GetFileNameWithoutExtension(path);

        if (!filename.StartsWith("valueset", StringComparison.OrdinalIgnoreCase))
            return (null, "The URL path does not appear to reference a ValueSet (expected 'valueset-' prefix in the filename).");

        var jsonPath = Path.ChangeExtension(path, ".json");
        var jsonUri = new UriBuilder(uri) { Path = jsonPath, Fragment = "" }.Uri;
        return (jsonUri.AbsoluteUri, null);
    }

    /// <summary>
    /// Extracts the canonical URLs of CodeSystems referenced in the ValueSet's compose includes.
    /// </summary>
    public static IReadOnlyList<string> GetReferencedCodeSystemUrls(ValueSet valueSet)
    {
        var urls = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        if (valueSet.Compose?.Include is not null)
        {
            foreach (var include in valueSet.Compose.Include)
            {
                if (!string.IsNullOrEmpty(include.System))
                    urls.Add(include.System);
            }
        }

        if (valueSet.Compose?.Exclude is not null)
        {
            foreach (var exclude in valueSet.Compose.Exclude)
            {
                if (!string.IsNullOrEmpty(exclude.System))
                    urls.Add(exclude.System);
            }
        }

        return urls.ToList();
    }

    /// <summary>
    /// Well-known external terminology system URLs that should never be imported
    /// from the FHIR core build. These are large, externally governed code systems
    /// (SNOMED CT, LOINC, etc.) or infrastructure systems (MIME types, BCP-47 languages)
    /// that do not belong in a THO repository.
    /// </summary>
    private static readonly HashSet<string> ExcludedCodeSystemUrls = new(StringComparer.OrdinalIgnoreCase)
    {
        // SNOMED CT
        "http://snomed.info/sct",
        // LOINC
        "http://loinc.org",
        // MIME types (BCP-13)
        "urn:ietf:bcp:13",
        // Languages (BCP-47)
        "urn:ietf:bcp:47",
        // UCUM units
        "http://unitsofmeasure.org",
        // ICD-10
        "http://hl7.org/fhir/sid/icd-10",
        "http://hl7.org/fhir/sid/icd-10-cm",
        // ICD-9
        "http://hl7.org/fhir/sid/icd-9-cm",
        // CPT
        "http://www.ama-assn.org/go/cpt",
        // RxNorm
        "http://www.nlm.nih.gov/research/umls/rxnorm",
        // CVX (vaccines)
        "http://hl7.org/fhir/sid/cvx",
        // NDC
        "http://hl7.org/fhir/sid/ndc",
        // NUBC (revenue codes, etc.)
        "https://www.nubc.org/CodeSystem/RevenueCodes",
        // ISO 3166 country codes
        "urn:iso:std:iso:3166",
        // ISO 4217 currency codes
        "urn:iso:std:iso:4217",
    };

    /// <summary>
    /// Canonical URL prefixes for code systems that are already managed within
    /// the THO repository and should not be re-imported from the FHIR core build.
    /// </summary>
    private static readonly string[] ExcludedCodeSystemPrefixes =
    [
        "http://terminology.hl7.org/CodeSystem/",
    ];

    /// <summary>
    /// Returns true if the given canonical URL is a well-known external terminology
    /// or a THO-managed code system that should not be imported from the FHIR core build.
    /// </summary>
    public static bool IsExcludedCodeSystem(string canonicalUrl)
        => ExcludedCodeSystemUrls.Contains(canonicalUrl)
           || ExcludedCodeSystemPrefixes.Any(p => canonicalUrl.StartsWith(p, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Attempts to convert a CodeSystem canonical URL to a FHIR core build download URL.
    /// For example, "http://hl7.org/fhir/action-type" becomes
    /// "https://build.fhir.org/codesystem-action-type.json".
    /// Returns null if the URL does not appear to be a FHIR core CodeSystem,
    /// or if it matches a well-known external terminology (SNOMED, LOINC, MIME types,
    /// languages, etc.).
    /// </summary>
    public static string? CanonicalToFhirBuildUrl(string canonicalUrl)
    {
        // Block well-known external terminologies
        if (IsExcludedCodeSystem(canonicalUrl))
            return null;

        // Typical pattern: http://hl7.org/fhir/{code-system-name}
        const string fhirPrefix = "http://hl7.org/fhir/";
        if (!canonicalUrl.StartsWith(fhirPrefix, StringComparison.OrdinalIgnoreCase))
            return null;

        var suffix = canonicalUrl[fhirPrefix.Length..].TrimEnd('/');

        // Skip nested paths (e.g. http://hl7.org/fhir/sid/icd-10)
        if (suffix.Contains('/'))
            return null;

        return $"https://build.fhir.org/codesystem-{suffix}.json";
    }

    /// <summary>
    /// Executes the full import: download, validate, save, update manifest, append provenance.
    /// Optionally imports referenced CodeSystem(s).
    /// </summary>
    public async Task<ImportValueSetResult> ImportAsync(
        string url,
        string destinationFolder,
        string changeComment,
        string? historyFile,
        string? authorName,
        string? custodianName,
        bool importReferencedCodeSystems,
        CancellationToken cancellationToken = default)
    {
        // 1. Normalize URL to JSON
        var (jsonUrl, urlError) = NormalizeUrl(url);
        if (jsonUrl is null)
            return new ImportValueSetResult(false, null, null, urlError, []);

        // 2. Download
        string json;
        try
        {
            json = await _httpClient.GetStringAsync(jsonUrl, cancellationToken);
        }
        catch (HttpRequestException ex)
        {
            _logger.LogWarning(ex, "Failed to download ValueSet from {Url}", jsonUrl);
            return new ImportValueSetResult(false, null, null, $"Failed to download: {ex.Message}", []);
        }

        // 3. Parse and verify it's a ValueSet
        ValueSet valueSet;
        try
        {
            valueSet = _jsonParser.Parse<ValueSet>(json);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Downloaded resource is not a valid ValueSet");
            return new ImportValueSetResult(false, null, null, $"Downloaded resource is not a valid ValueSet: {ex.Message}", []);
        }

        if (string.IsNullOrEmpty(valueSet.Id))
            return new ImportValueSetResult(false, null, null, "The ValueSet has no id element.", []);

        // 3b. Reject duplicate — a ValueSet with this id already exists in the repo
        var existing = _thoFileService.GetValueSetIndexEntry(valueSet.Id);
        if (existing is not null)
        {
            _logger.LogWarning("ValueSet {Id} already exists at {Path}", valueSet.Id, existing.FilePath);
            return new ImportValueSetResult(false, valueSet.Id, null,
                $"A ValueSet with id '{valueSet.Id}' already exists in the repository ({existing.FilePath}).", []);
        }

        // 4. Optionally import referenced CodeSystems
        var codeSystemResults = new List<ImportCodeSystemResult>();
        if (importReferencedCodeSystems)
        {
            var referencedUrls = GetReferencedCodeSystemUrls(valueSet);
            foreach (var canonicalUrl in referencedUrls)
            {
                var buildUrl = CanonicalToFhirBuildUrl(canonicalUrl);
                if (buildUrl is null)
                {
                    _logger.LogInformation(
                        "Skipping CodeSystem {Url} — not a FHIR core canonical URL", canonicalUrl);
                    continue;
                }

                // Check if it already exists by scanning the index for this canonical URL
                var existingCs = _thoFileService.GetCodeSystemIndex()
                    .FirstOrDefault(cs => string.Equals(cs.Url, canonicalUrl, StringComparison.OrdinalIgnoreCase));
                if (existingCs is not null)
                {
                    _logger.LogInformation(
                        "CodeSystem {Url} already exists as {Id}, skipping import", canonicalUrl, existingCs.Id);
                    codeSystemResults.Add(new ImportCodeSystemResult(
                        true, existingCs.Id, existingCs.FilePath, null));
                    continue;
                }

                var csResult = await _importCodeSystemService.ImportAsync(
                    buildUrl,
                    destinationFolder,
                    $"Imported as dependency of ValueSet {valueSet.Id}: {changeComment}",
                    historyFile,
                    authorName,
                    custodianName,
                    cancellationToken);

                codeSystemResults.Add(csResult);

                if (!csResult.Success)
                {
                    _logger.LogWarning(
                        "Failed to import referenced CodeSystem from {Url}: {Error}",
                        buildUrl, csResult.ErrorMessage);
                }
            }
        }

        // 5. Save as XML to destination folder
        var valueSetDir = Path.Combine(_settings.Path, destinationFolder, "valueSets");
        Directory.CreateDirectory(valueSetDir);

        var fileName = $"ValueSet-{valueSet.Id}.xml";
        var filePath = Path.Combine(valueSetDir, fileName);

        var xml = _xmlSerializer.SerializeToString(valueSet);
        await File.WriteAllTextAsync(filePath, "<?xml version=\"1.0\" encoding=\"UTF-8\"?>\n" + xml, cancellationToken);

        _logger.LogInformation("Saved imported ValueSet {Id} to {Path}", valueSet.Id, filePath);

        // 6. Append to rendering manifest
        AppendToRenderingManifest(destinationFolder, valueSet.Id);

        // 7. Append provenance to history bundle
        AppendProvenanceToHistory(historyFile, valueSet.Id, changeComment, authorName, custodianName);

        // 8. Invalidate the index so the new resource is picked up
        _thoFileService.InvalidateIndex();

        return new ImportValueSetResult(true, valueSet.Id, filePath, null, codeSystemResults);
    }

    private void AppendToRenderingManifest(string destinationFolder, string valueSetId)
    {
        var manifestPath = _thoFileService.GetRenderingManifestPath(destinationFolder);
        if (manifestPath is null)
        {
            _logger.LogWarning("No rendering manifest found for folder {Folder}", destinationFolder);
            return;
        }

        var reference = $"ValueSet/{valueSetId}";

        try
        {
            var content = File.ReadAllText(manifestPath);
            var list = new FhirXmlParser().Parse<Hl7.Fhir.Model.List>(content);

            var alreadyExists = list.Entry?.Any(e =>
                e.Item?.Reference == reference) == true;

            if (!alreadyExists)
            {
                list.Entry ??= [];
                list.Entry.Add(new Hl7.Fhir.Model.List.EntryComponent
                {
                    Item = new ResourceReference
                    {
                        Reference = reference,
                        Type = "ValueSet"
                    }
                });

                var xmlOut = new FhirXmlSerializer(new SerializerSettings { Pretty = true })
                    .SerializeToString(list);
                File.WriteAllText(manifestPath, "<?xml version=\"1.0\" encoding=\"UTF-8\"?>\n" + xmlOut);
                _logger.LogInformation("Appended {Reference} to manifest {Path}", reference, manifestPath);
            }
            else
            {
                _logger.LogInformation("Manifest already contains {Reference}", reference);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to update rendering manifest at {Path}", manifestPath);
        }
    }

    private void AppendProvenanceToHistory(
        string? historyFile,
        string valueSetId,
        string changeComment,
        string? authorName,
        string? custodianName)
    {
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
            var newFileName = $"utgrel-import-{timestamp}.json";
            historyPath = Path.Combine(historyDir, newFileName);

            var newBundle = new Bundle
            {
                Id = $"hx-import-{timestamp}",
                Type = Bundle.BundleType.Collection,
                Entry = []
            };

            var bundleJson = _jsonSerializer.SerializeToString(newBundle);
            File.WriteAllText(historyPath, bundleJson);
            _logger.LogInformation("Created new history bundle: {Path}", historyPath);
        }

        try
        {
            var content = File.ReadAllText(historyPath);
            var bundle = new FhirJsonParser().Parse<Bundle>(content);

            var now = DateTimeOffset.UtcNow;
            var provenanceId = $"hx-import-{valueSetId}-{now:yyyyMMdd}";

            var provenance = new Provenance
            {
                Id = provenanceId,
                Target = [new ResourceReference($"ValueSet/{valueSetId}")],
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
                            Code = "CREATE"
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

            var bundleJson = _jsonSerializer.SerializeToString(bundle);
            File.WriteAllText(historyPath, bundleJson);
            _logger.LogInformation("Appended provenance {Id} to history bundle {Path}", provenanceId, historyPath);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to update history bundle at {Path}", historyPath);
        }
    }
}
