using System.Text.Json;
using EmbodySense.Core.Application.HumanInput.Lifecycle.Models;
using EmbodySense.Core.Common.HumanInput.Models;
using EmbodySense.Core.Common.Workspace;
using EmbodySense.Core.Persistence.Capabilities;
using EmbodySense.Core.Persistence.HumanInput.Requests;
using EmbodySense.Core.Persistence.Tests.HumanInput.Requests;
using EmbodySense.Core.Startup.HumanInput.Models;
using EmbodySense.Core.Startup.Runtime;
using EmbodySense.Core.Startup.Workspace;
using EmbodySense.Tests.Support;

namespace EmbodySense.Core.Startup.Tests.Runtime;

public sealed partial class AgentRuntimeFactoryTests
{
    [Fact]
    public async Task Human_input_projection_exposes_bounded_recipient_count_and_continuation_kind_without_private_binding()
    {
        using var workspace = new TestWorkspace();
        await WorkspaceInitializer.ForFileCapabilityTrustRoot(workspace.ServerStatePath).InitializeAsync(workspace.RootPath);
        var paths = new WorkspacePaths(workspace.RootPath);
        var store = new HumanInputRequestStore(paths, new FileCapabilityCatalogTrustProvider(workspace.ServerStatePath));
        var mutation = CreateFreshHumanInputMutation(workspace.RootPath, "request-projection", "version-projection", "create-projection", HumanInputRequestStoreTestData.HashA);
        Assert.Equal(HumanInputRequestLifecycleStoreCommitStatus.Committed, (await store.CommitAsync(mutation)).Status);

        await using var runtime = await CreateRuntimeAsync(workspace, AgentRuntimeSurface.Web);
        var read = await runtime.HumanInput.ReadAsync("request-projection");
        var posture = Assert.IsType<HumanInputRequestPosture>(read.Request);
        var serialized = JsonSerializer.Serialize(posture, new JsonSerializerOptions(JsonSerializerDefaults.Web));

        Assert.Equal(HumanInputRequestPostureReadStatus.Ready, read.Status);
        Assert.Equal(1, posture.Presentation.EligibleRespondentCount);
        Assert.Equal(HumanInputContinuationPolicyKind.BoundNodeAndCheckpointOnly, posture.Presentation.ContinuationPolicyKind);
        Assert.Contains("eligibleRespondentCount", serialized, StringComparison.Ordinal);
        Assert.Contains("continuationPolicyKind", serialized, StringComparison.Ordinal);
        Assert.DoesNotContain("user-one", serialized, StringComparison.Ordinal);
        Assert.DoesNotContain("role-one", serialized, StringComparison.Ordinal);
        Assert.DoesNotContain("route-one", serialized, StringComparison.Ordinal);
        Assert.DoesNotContain("node-one", serialized, StringComparison.Ordinal);
        Assert.DoesNotContain("checkpoint-one", serialized, StringComparison.Ordinal);
        Assert.DoesNotContain("workspace-sha256", serialized, StringComparison.Ordinal);
    }
}
