using EmbodySense.Core.Common.Loops.Admission.Models;

namespace EmbodySense.Core.Application.Loops.Admission.Models;

/// <summary>Requests one atomic append-only governed-loop admission outcome commit.</summary>
/// <param name="WorkspaceId">The server-owned canonical workspace scope.</param>
/// <param name="OperationId">The workspace-global admission operation identity.</param>
/// <param name="RequestHash">The trusted server-prepared canonical invocation-content identity.</param>
/// <param name="IntentHash">The canonical hash of the complete server-derived admission intent.</param>
/// <param name="ExpectedStoreGeneration">The exact store generation observed before durable intent.</param>
/// <param name="Outcome">The validated immutable admitted or rejected terminal outcome.</param>
public sealed record GovernedLoopAdmissionStoreMutation(
    string WorkspaceId,
    string OperationId,
    string RequestHash,
    string IntentHash,
    long ExpectedStoreGeneration,
    GovernedLoopAdmissionTerminalOutcome Outcome);
