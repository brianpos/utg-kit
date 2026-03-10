using Hl7.Fhir.Model;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using UtgKit.Core.Services;
using Xunit;

namespace UtgKit.Core.Tests.Services;

public class ThoFileServiceTests
{
    private readonly ThoFileService _service;

    public ThoFileServiceTests()
    {
        // Resolve testdata directory relative to the test execution directory
        // bin/Debug/net10.0 -> (5 levels up) -> repo root -> testdata
        var testDataPath = Path.GetFullPath(
            Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "testdata"));

        var settings = Options.Create(new ThoRepoSettings { Path = testDataPath });
        _service = new ThoFileService(settings, NullLogger<ThoFileService>.Instance);
    }

    #region CodeSystem - Load All

    [Fact]
    public void GetCodeSystemIndex_ReturnsAllCodeSystems()
    {
        var index = _service.GetCodeSystemIndex();

        // fhir/codeSystems: accepting-patients
        // v2/codeSystems: v2-0001, v2-0002, v2-0003, v2-0004, v2-0006
        Assert.Equal(6, index.Count);
    }

    [Fact]
    public void GetCodeSystemIndex_ContainsExpectedIds()
    {
        var index = _service.GetCodeSystemIndex();
        var ids = index.Select(e => e.Id).ToList();

        Assert.Contains("accepting-patients", ids);
        Assert.Contains("v2-0001", ids);
        Assert.Contains("v2-0002", ids);
        Assert.Contains("v2-0003", ids);
        Assert.Contains("v2-0004", ids);
        Assert.Contains("v2-0006", ids);
    }

    [Fact]
    public void GetCodeSystemIndex_EntriesContainMetadata()
    {
        var index = _service.GetCodeSystemIndex();
        var entry = index.Single(e => e.Id == "accepting-patients");

        Assert.Equal("http://terminology.hl7.org/CodeSystem/accepting-patients", entry.Url);
        Assert.Equal("1.0.0", entry.Version);
        Assert.Equal("AcceptingPatients", entry.Name);
        Assert.Equal("Accepting Patients", entry.Title);
        Assert.Equal(PublicationStatus.Active, entry.Status);
        Assert.Equal(4, entry.ConceptCount);
        Assert.Equal("pa", entry.Owner);
    }

    #endregion

    #region CodeSystem - Load Specific

    [Fact]
    public void LoadCodeSystem_ValidId_ReturnsCodeSystem()
    {
        var cs = _service.LoadCodeSystem("accepting-patients");

        Assert.NotNull(cs);
        Assert.Equal("accepting-patients", cs.Id);
        Assert.Equal("http://terminology.hl7.org/CodeSystem/accepting-patients", cs.Url);
        Assert.Equal(4, cs.Concept.Count);
    }

    [Fact]
    public void LoadCodeSystem_V2Resource_ReturnsCorrectData()
    {
        var cs = _service.LoadCodeSystem("v2-0001");

        Assert.NotNull(cs);
        Assert.Equal("v2-0001", cs.Id);
        Assert.Equal("http://terminology.hl7.org/CodeSystem/v2-0001", cs.Url);
        Assert.Equal("2.0.0", cs.Version);
        Assert.Equal("AdministrativeSex", cs.Name);
    }

    [Fact]
    public void LoadCodeSystem_NonExistentId_ReturnsNull()
    {
        var cs = _service.LoadCodeSystem("non-existent-id");

        Assert.Null(cs);
    }

    [Fact]
    public void GetCodeSystemIndexEntry_ValidId_ReturnsEntry()
    {
        var entry = _service.GetCodeSystemIndexEntry("accepting-patients");

        Assert.NotNull(entry);
        Assert.Equal("accepting-patients", entry.Id);
    }

    [Fact]
    public void GetCodeSystemIndexEntry_NonExistentId_ReturnsNull()
    {
        var entry = _service.GetCodeSystemIndexEntry("does-not-exist");

        Assert.Null(entry);
    }

    #endregion

    #region ValueSet - Load All

    [Fact]
    public void GetValueSetIndex_ReturnsAllValueSets()
    {
        var index = _service.GetValueSetIndex();

        // fhir/valueSets: accepting-patients
        // v2/valueSets: v2-0001 through v2-0006
        Assert.Equal(7, index.Count);
    }

    [Fact]
    public void GetValueSetIndex_ContainsExpectedIds()
    {
        var index = _service.GetValueSetIndex();
        var ids = index.Select(e => e.Id).ToList();

        Assert.Contains("accepting-patients", ids);
        Assert.Contains("v2-0001", ids);
        Assert.Contains("v2-0002", ids);
        Assert.Contains("v2-0003", ids);
        Assert.Contains("v2-0004", ids);
        Assert.Contains("v2-0005", ids);
        Assert.Contains("v2-0006", ids);
    }

    [Fact]
    public void GetValueSetIndex_EntriesContainMetadata()
    {
        var index = _service.GetValueSetIndex();
        var entry = index.Single(e => e.Id == "accepting-patients");

        Assert.Equal("http://terminology.hl7.org/ValueSet/accepting-patients", entry.Url);
        Assert.Equal("1.0.0", entry.Version);
        Assert.Equal("AcceptingPatients", entry.Name);
        Assert.Equal(PublicationStatus.Active, entry.Status);
        Assert.Equal("pa", entry.Owner);
    }

    #endregion

    #region ValueSet - Load Specific

    [Fact]
    public void LoadValueSet_ValidId_ReturnsValueSet()
    {
        var vs = _service.LoadValueSet("accepting-patients");

        Assert.NotNull(vs);
        Assert.Equal("accepting-patients", vs.Id);
        Assert.Equal("http://terminology.hl7.org/ValueSet/accepting-patients", vs.Url);
    }

    [Fact]
    public void LoadValueSet_ValidId_ComposeIsPresent()
    {
        var vs = _service.LoadValueSet("accepting-patients");

        Assert.NotNull(vs);
        Assert.NotNull(vs.Compose);
        Assert.Single(vs.Compose.Include);
        Assert.Equal("http://terminology.hl7.org/CodeSystem/accepting-patients",
            vs.Compose.Include[0].System);
    }

    [Fact]
    public void LoadValueSet_V2Resource_ReturnsCorrectData()
    {
        var vs = _service.LoadValueSet("v2-0001");

        Assert.NotNull(vs);
        Assert.Equal("v2-0001", vs.Id);
        Assert.Equal("http://terminology.hl7.org/ValueSet/v2-0001", vs.Url);
        Assert.Equal("2.0.0", vs.Version);
        Assert.Equal("Hl7VSAdministrativeSex", vs.Name);
    }

    [Fact]
    public void LoadValueSet_NonExistentId_ReturnsNull()
    {
        var vs = _service.LoadValueSet("non-existent-id");

        Assert.Null(vs);
    }

    [Fact]
    public void GetValueSetIndexEntry_ValidId_ReturnsEntry()
    {
        var entry = _service.GetValueSetIndexEntry("v2-0001");

        Assert.NotNull(entry);
        Assert.Equal("v2-0001", entry.Id);
    }

    [Fact]
    public void GetValueSetIndexEntry_NonExistentId_ReturnsNull()
    {
        var entry = _service.GetValueSetIndexEntry("does-not-exist");

        Assert.Null(entry);
    }

    #endregion
}
