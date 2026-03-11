using Hl7.Fhir.Model;
using Hl7.Fhir.Serialization;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace UtgKit.Core.Services;

/// <summary>
/// Result of a CodeSystem import operation.
/// </summary>
public sealed record ImportCodeSystemResult(
    bool Success,
    string? CodeSystemId,
    string? SavedFilePath,
    string? ErrorMessage);

/// <summary>
/// Downloads a CodeSystem from the FHIR core build, saves it into the local
/// THO repository, updates the rendering manifest, and appends a provenance
/// entry to the selected history bundle.
/// </summary>
public class ImportCodeSystemService
{
    private readonly ThoRepoSettings _settings;
    private readonly ThoFileService _thoFileService;
    private readonly HttpClient _httpClient;
    private readonly FhirJsonParser _jsonParser = new();
    private readonly FhirXmlSerializer _xmlSerializer = new(new SerializerSettings { Pretty = true });
    private readonly ILogger<ImportCodeSystemService> _logger;

    public ImportCodeSystemService(
        IOptions<ThoRepoSettings> settings,
        ThoFileService thoFileService,
        HttpClient httpClient,
        ILogger<ImportCodeSystemService> logger)
    {
        _settings = settings.Value;
        _thoFileService = thoFileService;
        _httpClient = httpClient;
        _logger = logger;
    }

    /// <summary>
    /// Normalizes a FHIR build URL to its JSON variant.
    /// Accepts .html, .xml, or .json URLs and returns the .json form.
    /// Validates the path contains "codesystem" to confirm the resource type.
    /// </summary>
    public static (string? JsonUrl, string? Error) NormalizeUrl(string url)
    {
        if (!Uri.TryCreate(url.Trim(), UriKind.Absolute, out var uri))
            return (null, "Invalid URL.");

        var path = uri.AbsolutePath;
        var filename = Path.GetFileNameWithoutExtension(path);

        if (!filename.StartsWith("codesystem", StringComparison.OrdinalIgnoreCase))
            return (null, "The URL path does not appear to reference a CodeSystem (expected 'codesystem-' prefix in the filename).");

        var jsonPath = Path.ChangeExtension(path, ".json");
        var jsonUri = new UriBuilder(uri) { Path = jsonPath, Fragment = "" }.Uri;
        return (jsonUri.AbsoluteUri, null);
    }

    /// <summary>
    /// Executes the full import: download, validate, save, update manifest, append provenance.
    /// </summary>
    public async Task<ImportCodeSystemResult> ImportAsync(
        string url,
        string destinationFolder,
        string changeComment,
        string? historyFile,
        string? authorName,
        string? custodianName,
        CancellationToken cancellationToken = default)
    {
        // 1. Normalize URL to JSON
        var (jsonUrl, urlError) = NormalizeUrl(url);
        if (jsonUrl is null)
            return new ImportCodeSystemResult(false, null, null, urlError);

        // 2. Download
        string json;
        try
        {
            json = await _httpClient.GetStringAsync(jsonUrl, cancellationToken);
        }
        catch (HttpRequestException ex)
        {
            _logger.LogWarning(ex, "Failed to download CodeSystem from {Url}", jsonUrl);
            return new ImportCodeSystemResult(false, null, null, $"Failed to download: {ex.Message}");
        }

        // 3. Parse and verify it's a CodeSystem
        CodeSystem codeSystem;
        try
        {
            codeSystem = _jsonParser.Parse<CodeSystem>(json);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Downloaded resource is not a valid CodeSystem");
            return new ImportCodeSystemResult(false, null, null, $"Downloaded resource is not a valid CodeSystem: {ex.Message}");
        }

        if (string.IsNullOrEmpty(codeSystem.Id))
            return new ImportCodeSystemResult(false, null, null, "The CodeSystem has no id element.");

        // 3b. Reject duplicate — a CodeSystem with this id already exists in the repo
        var existing = _thoFileService.GetCodeSystemIndexEntry(codeSystem.Id);
        if (existing is not null)
        {
            _logger.LogWarning("CodeSystem {Id} already exists at {Path}", codeSystem.Id, existing.FilePath);
            return new ImportCodeSystemResult(false, codeSystem.Id, null,
                $"A CodeSystem with id '{codeSystem.Id}' already exists in the repository ({existing.FilePath}).");
        }

        // 4. Save as XML to destination folder — suppress file watcher to avoid re-indexing our own writes
        using var _ = _thoFileService.SuppressWatcher();

        var codeSystemDir = Path.Combine(_settings.Path, destinationFolder, "codeSystems");
        Directory.CreateDirectory(codeSystemDir);

        var fileName = $"CodeSystem-{codeSystem.Id}.xml";
        var filePath = Path.Combine(codeSystemDir, fileName);

        var xml = _xmlSerializer.SerializeToString(codeSystem);
        await File.WriteAllTextAsync(filePath, "<?xml version=\"1.0\" encoding=\"UTF-8\"?>\n" + xml, cancellationToken);

        _logger.LogInformation("Saved imported CodeSystem {Id} to {Path}", codeSystem.Id, filePath);

        // 5. Append to rendering manifest
        AppendToRenderingManifest(destinationFolder, codeSystem.Id);

        // 6. Append provenance to history bundle
        _thoFileService.AppendProvenanceEntry(
            $"CodeSystem/{codeSystem.Id}",
            "CREATE",
            changeComment,
            historyFile,
            authorName,
            custodianName);

        // 7. Invalidate the index so the new resource is picked up
        _thoFileService.InvalidateIndex();

        return new ImportCodeSystemResult(true, codeSystem.Id, filePath, null);
    }

    private void AppendToRenderingManifest(string destinationFolder, string codeSystemId)
    {
        var manifestPath = _thoFileService.GetRenderingManifestPath(destinationFolder);
        if (manifestPath is null)
        {
            _logger.LogWarning("No rendering manifest found for folder {Folder}", destinationFolder);
            return;
        }

        var reference = $"CodeSystem/{codeSystemId}";

        // Load the manifest as a FHIR List and check for duplicates
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
                        Type = "CodeSystem"
                    }
                });

                var xml = new FhirXmlSerializer(new SerializerSettings { Pretty = true })
                    .SerializeToString(list);
                File.WriteAllText(manifestPath, "<?xml version=\"1.0\" encoding=\"UTF-8\"?>\n" + xml);
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

    }
