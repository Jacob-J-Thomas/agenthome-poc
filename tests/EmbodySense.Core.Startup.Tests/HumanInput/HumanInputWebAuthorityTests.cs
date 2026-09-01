using EmbodySense.Core.Startup.HumanInput;
using EmbodySense.Core.Startup.Runtime.Models;
using EmbodySense.Core.Startup.Workspace;

namespace EmbodySense.Core.Startup.Tests.HumanInput;

public sealed class HumanInputWebAuthorityTests
{
    [Fact]
    public void Web_authority_pins_actor_and_derives_deterministic_value_free_evidence()
    {
        var workspaceId = HumanInputWebAuthority.GetWorkspaceId("/tmp/human-input-authority");
        var now = new DateTimeOffset(2026, 9, 1, 12, 0, 0, TimeSpan.Zero);

        var lifecycle = HumanInputWebAuthority.AuthorizeLifecycle(true, "operation-1", "request-hash", workspaceId, now);
        var lifecycleReplay = HumanInputWebAuthority.AuthorizeLifecycle(true, "operation-1", "request-hash", workspaceId, now);
        var response = HumanInputWebAuthority.AuthenticateResponse(true, "operation-2", "command-hash", workspaceId, now);

        Assert.Equal(WorkspaceActors.Web, lifecycle.ActorId!.Value);
        Assert.Equal(WorkspaceActors.Web, response.ActorId!.Value);
        Assert.Equal(lifecycle.AuthorityEvidenceHash, lifecycleReplay.AuthorityEvidenceHash);
        Assert.NotEqual(lifecycle.AuthorityEvidenceHash, response.AuthenticationEvidenceHash);
        Assert.Equal(64, lifecycle.AuthorityEvidenceHash.Length);
        Assert.NotNull(HumanInputWebAuthority.GetWebActor());
    }

    [Fact]
    public void Web_authority_denies_unauthenticated_operations_without_actor_or_evidence()
    {
        var now = new DateTimeOffset(2026, 9, 1, 12, 0, 0, TimeSpan.Zero);
        var lifecycle = HumanInputWebAuthority.AuthorizeLifecycle(false, "operation-1", "hash", "workspace", now);
        var response = HumanInputWebAuthority.AuthenticateResponse(false, "operation-1", "hash", "workspace", now);

        Assert.Equal(AgentRuntimeHumanInputAuthorityStatus.Denied, lifecycle.Status);
        Assert.Equal(AgentRuntimeHumanInputAuthorityStatus.Denied, response.Status);
        Assert.Null(lifecycle.ActorId);
        Assert.Null(response.ActorId);
        Assert.Empty(lifecycle.AuthorityEvidenceHash);
        Assert.Empty(response.AuthenticationEvidenceHash);
    }
}
