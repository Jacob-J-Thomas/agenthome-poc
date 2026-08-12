using EmbodySense.Core.Common.Loops.Admission.Models;
using EmbodySense.Core.Common.Loops.Execution;
using EmbodySense.Core.Common.Loops.Revisions.Models;

namespace EmbodySense.Core.Application.Loops.Execution.Custom.Models;

/// <summary>Provides complete immutable canonical proof for one conversation-publication commit boundary.</summary>
/// <param name="AdmissionReceipt">The exact successful admission receipt retained by the canonical run.</param>
/// <param name="ExecutionBinding">The exact run, revision, and execution generation.</param>
/// <param name="GraphArtifact">The exact immutable graph artifact retained by the run.</param>
/// <param name="NodeId">The exact success-Exit node identity.</param>
/// <param name="NodeAttempt">The exact positive node-attempt number.</param>
/// <param name="PublicationOperationId">The stable identity-bearing conversation publication operation.</param>
public sealed record GovernedLoopConversationPublicationAuthorityRequest(
    GovernedLoopAdmissionReceipt AdmissionReceipt,
    GovernedLoopExecutionBinding ExecutionBinding,
    GovernedLoopGraphRevisionArtifact GraphArtifact,
    string NodeId,
    int NodeAttempt,
    string PublicationOperationId);
