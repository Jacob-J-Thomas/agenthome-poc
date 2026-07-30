using EmbodySense.Core.Common.Governance.Permissions;

namespace EmbodySense.Core.Common.Tests;

public sealed class PermissionsDocumentVersionTests
{
    [Fact]
    public void FromJson_accepts_only_an_explicit_current_version()
    {
        Assert.Null(PermissionsDocument.FromJson("{}"));
        Assert.Null(PermissionsDocument.FromJson("""{"scope":"single-file-system-directory-level","approved":[],"denied":[]}"""));

        var document = Assert.IsType<PermissionsDocument>(PermissionsDocument.FromJson("""{"version":1}"""));

        Assert.Equal(PermissionsDocument.CurrentVersion, document.Version);
    }

    [Theory]
    [InlineData("""{"version":null}""")]
    [InlineData("""{"version":"1"}""")]
    [InlineData("""{"version":1.0}""")]
    [InlineData("""{"version":2}""")]
    [InlineData("""{"version":1,"Version":1}""")]
    public void FromJson_rejects_malformed_unsupported_or_duplicate_versions(string json)
    {
        Assert.Null(PermissionsDocument.FromJson(json));
    }
}
