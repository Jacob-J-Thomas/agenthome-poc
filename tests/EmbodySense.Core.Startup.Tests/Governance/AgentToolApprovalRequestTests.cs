using System.Text.Json;
using EmbodySense.Core.Common.Governance.Permissions;
using EmbodySense.Core.Common.Governance.Permissions.Models;
using EmbodySense.Core.Common.Governance.Tools.Models;
using EmbodySense.Core.Startup.Governance;

namespace EmbodySense.Core.Startup.Tests.Governance;

public sealed class AgentToolApprovalRequestTests
{
    [Fact]
    public void WorkspaceMutationProjectionNeverExposesCallerAbsoluteOrPolicyPaths()
    {
        const string TargetCanary = "/private/secret-workspace/customer.txt";
        const string ResolvedCanary = "/private/secret-workspace/customer.txt";
        const string PolicyCanary = "/private/secret-workspace/**";
        var source = new ToolApprovalRequest(
            "request-alpha",
            new ToolRequest(ToolCommand.Write, TargetCanary, "secret-value"),
            ResolvedCanary,
            FileSystemOperation.Create,
            PermissionEvaluation.RequiresApproval(PolicyCanary, $"Approval required for {PolicyCanary}."),
            new string('a', 64));

        var projected = AgentToolApprovalRequest.FromToolApprovalRequest(source);
        var serialized = JsonSerializer.Serialize(projected);

        Assert.StartsWith("workspace-target-", projected.TargetPath, StringComparison.Ordinal);
        Assert.Equal(projected.TargetPath, projected.ResolvedPath);
        Assert.Equal("(protected workspace policy)", projected.MatchedPath);
        Assert.Equal("Governed workspace mutation permission decision: requiresapproval.", projected.Reason);
        Assert.DoesNotContain(TargetCanary, serialized, StringComparison.Ordinal);
        Assert.DoesNotContain(PolicyCanary, serialized, StringComparison.Ordinal);
        Assert.DoesNotContain("secret-value", serialized, StringComparison.Ordinal);
    }
}
