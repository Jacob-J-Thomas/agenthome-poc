using EmbodySense.Core.Common.Inference.Profiles.Models;
using EmbodySense.Core.Common.Loops.Admission.Models;

namespace EmbodySense.Core.Application.Inference.Profiles.Models;

/// <summary>Requests current revalidation and an atomic server-derived pre-transport reservation for the admitted primary.</summary>
public sealed record GovernedModelAttemptAdmissionRequest(
    GovernedModelRoutingAdmissionSnapshot RoutingAdmission,
    GovernedLoopAdmissionReceipt AdmissionReceipt,
    string RunId,
    long ExecutionGeneration,
    string NodeId,
    string NodeTypeId,
    int PlanOrdinal,
    int ActivationOrdinal,
    int VisitOrdinal,
    string AttemptOperationId,
    int AttemptNumber,
    string RequestedPrimaryPinHash)
{
    /// <summary>Gets the optional retry-derived per-attempt ceiling that narrows the admitted model reservation before transport.</summary>
    public GovernedModelUsageCeiling? RetryUsageCeiling { get; init; }
}
