using EmbodySense.Core.Common.Governance.Permissions.Models;
using EmbodySense.Core.Common.Governance.Tools;
using EmbodySense.Core.Common.Governance.Tools.Models;

namespace EmbodySense.Core.Common.Tests;

public sealed class WorkspaceMutationEvidenceProjectionTests
{
    [Fact]
    public void ProjectResult_removes_semantic_content_absolute_paths_and_free_text_from_mutation_evidence()
    {
        const string Secret = "semantic-secret";
        const string Absolute = "/private/workspace/shared/note.txt";
        var request = new ToolRequest(ToolCommand.Write, "shared/note.txt", Secret, Secret, "correlation-1");
        var governance = new ToolGovernanceEvidence(
            ToolAuthorityDecision.Allowed,
            Absolute,
            PermissionDecision.Allow,
            Absolute,
            Absolute,
            "policy-hash",
            ToolApprovalDecision.NotRequired,
            "policy",
            Absolute);
        var result = new ToolResult(ToolExecutionOutcome.Succeeded, Secret, new string('a', 32), Absolute, request, governance);

        var projected = WorkspaceMutationEvidenceProjection.ProjectResult(result);

        Assert.Null(projected.Request.Content);
        Assert.Null(projected.Request.Pattern);
        Assert.Equal("shared/note.txt", projected.ResolvedPath);
        Assert.Equal("governed workspace mutation succeeded", projected.OutputText);
        Assert.Null(projected.Governance!.PermissionMatchedPath);
        Assert.DoesNotContain(Secret, projected.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain(Absolute, projected.ToString(), StringComparison.Ordinal);
        Assert.Equal("policy-hash", projected.Governance.PermissionPolicyHash);
        Assert.Equal(ToolAuthorityDecision.Allowed, projected.Governance.AuthorityDecision);
        Assert.Equal(PermissionDecision.Allow, projected.Governance.PermissionDecision);
    }

    [Fact]
    public void ProjectResult_preserves_observation_evidence()
    {
        var request = new ToolRequest(ToolCommand.Read, "shared/note.txt", Pattern: "needle");
        var result = new ToolResult(ToolExecutionOutcome.Succeeded, "content", new string('a', 32), "/workspace/shared/note.txt", request);

        Assert.Same(result, WorkspaceMutationEvidenceProjection.ProjectResult(result));
    }
}
