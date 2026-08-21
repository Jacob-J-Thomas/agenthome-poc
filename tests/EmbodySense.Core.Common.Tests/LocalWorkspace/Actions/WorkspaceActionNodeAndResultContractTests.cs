using EmbodySense.Core.Common.LocalWorkspace.Actions;
using EmbodySense.Core.Common.LocalWorkspace.Actions.Models;
using EmbodySense.Core.Common.Loops.Models.Custom.Graph;

namespace EmbodySense.Core.Common.Tests.LocalWorkspace.Actions;

public sealed class WorkspaceActionNodeAndResultContractTests
{
    [Theory]
    [InlineData(WorkspaceActionKind.Append, WorkspaceActionOperationIds.Append)]
    [InlineData(WorkspaceActionKind.Write, WorkspaceActionOperationIds.Write)]
    [InlineData(WorkspaceActionKind.Delete, WorkspaceActionOperationIds.Delete)]
    public void Exact_action_descriptors_are_closed_and_operation_bound(WorkspaceActionKind expectedKind, string expectedTypeId)
    {
        var descriptor = expectedKind switch
        {
            WorkspaceActionKind.Append => WorkspaceActionNodeDescriptors.Append,
            WorkspaceActionKind.Write => WorkspaceActionNodeDescriptors.Write,
            WorkspaceActionKind.Delete => WorkspaceActionNodeDescriptors.Delete,
            _ => throw new ArgumentOutOfRangeException(nameof(expectedKind)),
        };

        Assert.Equal(GovernedLoopNodeKind.Action, descriptor.Kind);
        Assert.Equal(expectedTypeId, descriptor.TypeId);
        Assert.Equal(1, descriptor.Version);
        Assert.True(WorkspaceActionNodeDescriptors.TryResolve(descriptor, out var resolved));
        Assert.Equal(expectedKind, resolved);
        Assert.False(WorkspaceActionNodeDescriptors.TryResolve(descriptor with { Version = 2 }, out _));
    }

    [Theory]
    [InlineData(WorkspaceActionResultStatus.Committed, "committed")]
    [InlineData(WorkspaceActionResultStatus.Replayed, "replayed")]
    public void Result_contract_round_trips_only_exact_value_free_evidence(WorkspaceActionResultStatus status, string encodedStatus)
    {
        var evidenceId = "after-" + new string('a', 64);
        var result = WorkspaceActionResultContract.Create(status, evidenceId, 1);

        var canonical = WorkspaceActionResultContract.Encode(result);

        Assert.Equal($"{{\"afterEvidenceId\":\"{evidenceId}\",\"effectGeneration\":1,\"schemaVersion\":1,\"status\":\"{encodedStatus}\"}}", canonical);
        Assert.True(WorkspaceActionResultContract.TryParse(canonical, out var replay));
        Assert.Equal(result, replay);
        Assert.False(WorkspaceActionResultContract.TryParse(canonical + " ", out _));
        Assert.False(WorkspaceActionResultContract.TryParse(canonical.Replace(new string('a', 64), "A" + new string('a', 63), StringComparison.Ordinal), out _));
    }
}
