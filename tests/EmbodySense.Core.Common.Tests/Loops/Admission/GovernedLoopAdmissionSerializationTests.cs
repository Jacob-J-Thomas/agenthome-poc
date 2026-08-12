using System.Text.Json;
using EmbodySense.Core.Common.Loops.Admission;
using EmbodySense.Core.Common.Loops.Admission.Models;

namespace EmbodySense.Core.Common.Tests.Loops.Admission;

public sealed class GovernedLoopAdmissionSerializationTests
{
    [Fact]
    public void Public_json_projection_preserves_exact_nonsecret_admission_evidence()
    {
        var intent = GovernedLoopAdmissionTestFixture.Intent();
        var outcome = GovernedLoopAdmissionTestFixture.AdmittedOutcome(intent);
        var json = JsonSerializer.Serialize(outcome);
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        var projectedIntent = root.GetProperty("Intent");
        var evidence = root.GetProperty("Receipt").GetProperty("Evidence");

        Assert.Equal(GovernedLoopAdmissionLimits.CurrentSchemaVersion, root.GetProperty("SchemaVersion").GetInt32());
        Assert.Equal(intent.WorkspaceId, projectedIntent.GetProperty("WorkspaceId").GetString());
        Assert.Equal(intent.OperationId, projectedIntent.GetProperty("OperationId").GetString());
        Assert.Equal(intent.RequestHash, projectedIntent.GetProperty("RequestHash").GetString());
        Assert.Equal(intent.Publication.Revision.GraphId, projectedIntent.GetProperty("Publication").GetProperty("Revision").GetProperty("GraphId").GetString());
        Assert.Equal(intent.AuthorityGrant.GrantId.Value, projectedIntent.GetProperty("AuthorityGrant").GetProperty("GrantId").GetProperty("Value").GetString());
        Assert.Equal(intent.Role.Identity.RoleId, projectedIntent.GetProperty("Role").GetProperty("Identity").GetProperty("RoleId").GetString());
        Assert.Equal(intent.ActorId.Value, projectedIntent.GetProperty("ActorId").GetProperty("Value").GetString());
        Assert.Equal(intent.GraphArtifactHash, projectedIntent.GetProperty("GraphArtifactHash").GetString());
        Assert.Equal(intent.GraphLayoutHash, projectedIntent.GetProperty("GraphLayoutHash").GetString());
        Assert.Equal(intent.Publication.Revision.GraphId, evidence.GetProperty("Binding").GetProperty("Revision").GetProperty("GraphId").GetString());
        Assert.Equal(
            Enum.GetValues<GovernedLoopAdmissionEvidenceKind>().Count(kind => kind != GovernedLoopAdmissionEvidenceKind.Unknown),
            evidence.GetProperty("References").GetArrayLength());
        Assert.DoesNotContain("instructionSource", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("credential", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("secretValue", json, StringComparison.OrdinalIgnoreCase);
    }
}
