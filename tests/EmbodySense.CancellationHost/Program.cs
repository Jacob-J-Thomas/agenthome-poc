using EmbodySense.Core.Application.Loops.Models;
using EmbodySense.Core.Application.Loops;
using EmbodySense.Core.Application.ContextualRoles;
using EmbodySense.Core.Application.ContextualRoles.Models;
using EmbodySense.Core.Common.ContextualRoles;
using EmbodySense.Core.Common.ContextualRoles.Models;
using EmbodySense.Core.Common.Governance.Audit;
using EmbodySense.Core.Common.Workspace;
using EmbodySense.Core.Persistence.ContextualRoles;
using EmbodySense.Core.Persistence.ContextualRoles.Models;
using EmbodySense.Core.Persistence.Loops;
using System.Collections.Immutable;

if (args is ["hold-contextual-role", var contextualRoleWorkspaceRoot])
{
    return await HoldContextualRoleMutationAsync(contextualRoleWorkspaceRoot);
}

if (args is ["hold-control", var workspaceRoot, var kindText, var runId, var versionText, var operationId])
{
    return await HoldControlOperationAsync(workspaceRoot, kindText, runId, versionText, operationId);
}

if (args is [var cancellationWorkspaceRoot, var cancellationRunId])
{
    return await HostCancellationAsync(cancellationWorkspaceRoot, cancellationRunId);
}

return 2;

static async Task<int> HoldContextualRoleMutationAsync(string workspaceRoot)
{
    var now = DateTimeOffset.UtcNow;
    var revision = ContextualRoleRevisionContentHash.Apply(new ContextualRoleRevision(
        1,
        new ContextualRoleRevisionIdentity("reviewer", 1),
        string.Empty,
        "Reviewer",
        "Provide bounded review assistance.",
        ContextualRoleStatus.Published,
        new ContextualRoleProvenance("user-jake", now, now),
        new ContextualRoleWorkspaceApplicability(ImmutableArray.Create("workspace-one")),
        new ContextualRoleInstructionSourceReference(ContextualRoleInstructionSourceKind.RoleArtifact, "reviewer-source", ContextualRoleInstructionClassification.RoleInstruction),
        new ContextualRolePolicyMaxima(ImmutableArray<string>.Empty)));
    var request = ContextualRoleRevisionMutationRequestHash.Apply(new ContextualRoleRevisionMutationRequest("create-reviewer", string.Empty, ContextualRoleRevisionMutationKind.Create, "reviewer", "user-jake", revision, null, now));
    var options = new ContextualRoleRevisionStoreOptions
    {
        DurableBoundaryObserver = async (boundary, _) =>
        {
            if (boundary == ContextualRolePersistenceBoundary.IntentPublished)
            {
                Console.WriteLine("ready");
                await Console.Out.FlushAsync();
                Console.ReadLine();
            }
        }
    };
    var result = await new ContextualRoleRevisionStore(new WorkspacePaths(workspaceRoot), "workspace-one", options).MutateAsync(request);
    return result.Status == ContextualRoleRevisionMutationStatus.Accepted ? 0 : 3;
}

static async Task<int> HostCancellationAsync(string workspaceRoot, string runId)
{
    var paths = new WorkspacePaths(workspaceRoot);
    await using var gate = new CustomLoopWorkspaceExecutionGate(paths);
    using var cancellation = new CancellationTokenSource();
    using var registration = gate.RegisterActiveAttempt(runId, cancellation);
    Console.WriteLine("ready");
    await Console.Out.FlushAsync();
    try
    {
        await Task.Delay(Timeout.InfiniteTimeSpan, cancellation.Token);
    }
    catch (OperationCanceledException exception)
    {
        _ = registration.TryConfirmProviderInterruption(exception.CancellationToken);
        Console.WriteLine("interrupted");
        await Console.Out.FlushAsync();
    }

    _ = Console.ReadLine();
    return 0;
}

static async Task<int> HoldControlOperationAsync(string workspaceRoot, string kindText, string runId, string versionText, string operationId)
{
    if (!Enum.TryParse<CustomLoopControlKind>(kindText, ignoreCase: true, out var kind) || kind == CustomLoopControlKind.Unknown || !int.TryParse(versionText, out var expectedVersion))
    {
        return 2;
    }

    var now = DateTimeOffset.UtcNow.ToUniversalTime();
    var actor = AuditSchema.Actors.Web;
    var pending = new CustomLoopControlOperation(
        CustomLoopControlOperation.CurrentSchemaVersion,
        operationId,
        CustomLoopControlRequestHash.Compute(kind, runId, expectedVersion, operationId, actor),
        kind,
        runId,
        expectedVersion,
        actor,
        now,
        now,
        CustomLoopControlOperationState.Pending,
        CustomLoopControlStatus.Unknown,
        null,
        null,
        false,
        "The child process is holding the pre-transition control-operation execution lease.");
    var result = await new CustomLoopControlOperationStore(new WorkspacePaths(workspaceRoot)).BeginAsync(pending);
    using var lease = result.Lease;
    if (result.Status != CustomLoopControlOperationStoreStatus.Created || lease is null)
    {
        return 3;
    }

    Console.WriteLine("ready");
    await Console.Out.FlushAsync();
    _ = Console.ReadLine();
    return 0;
}
