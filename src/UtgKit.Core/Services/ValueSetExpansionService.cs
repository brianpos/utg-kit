using Hl7.Fhir.Model;
using Hl7.Fhir.Specification.Terminology;
using Microsoft.Extensions.Logging;

namespace UtgKit.Core.Services;

/// <summary>
/// Expands a ValueSet using the Firely SDK's <see cref="ValueSetExpander"/>
/// and the local THO repository as the source for referenced code systems.
/// </summary>
public class ValueSetExpansionService
{
    private readonly ThoResourceResolver _resolver;
    private readonly ILogger<ValueSetExpansionService> _logger;

    public ValueSetExpansionService(ThoResourceResolver resolver, ILogger<ValueSetExpansionService> logger)
    {
        _resolver = resolver;
        _logger = logger;
    }

    /// <summary>
    /// Attempts to expand the given <paramref name="valueSet"/> if it has no
    /// existing expansion. Returns <c>true</c> when an expansion was added.
    /// The ValueSet is modified in place.
    /// </summary>
    public bool TryExpand(ValueSet valueSet)
    {
        if (valueSet.Expansion?.Contains?.Count > 0)
            return false;

        try
        {
            var settings = new ValueSetExpanderSettings { ValueSetSource = _resolver };
            var expander = new ValueSetExpander(settings);
            expander.Expand(valueSet);
            return valueSet.Expansion?.Contains?.Count > 0;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Could not expand ValueSet {Id}", valueSet.Id);
            return false;
        }
    }
}
