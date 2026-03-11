using Hl7.Fhir.Model;
using Hl7.Fhir.Specification.Source;

namespace UtgKit.Core.Services;

/// <summary>
/// Resolves FHIR resources (CodeSystem, ValueSet) from the local THO repository
/// so the Firely SDK's ValueSet expander can look up referenced code systems.
/// </summary>
public class ThoResourceResolver : IAsyncResourceResolver
{
    private readonly ThoFileService _thoFileService;

    public ThoResourceResolver(ThoFileService thoFileService)
    {
        _thoFileService = thoFileService;
    }

    public Task<Resource?> ResolveByCanonicalUriAsync(string uri)
    {
        // Strip any version suffix (e.g. "|4.0.1")
        var bareUrl = uri.Contains('|') ? uri[..uri.IndexOf('|')] : uri;

        Resource? result = _thoFileService.LoadCodeSystemByUrl(bareUrl)
            ?? (Resource?)_thoFileService.LoadValueSetByUrl(bareUrl);

        return System.Threading.Tasks.Task.FromResult(result);
    }

    public System.Threading.Tasks.Task<Resource?> ResolveByUriAsync(string uri)
        => ResolveByCanonicalUriAsync(uri);
}
