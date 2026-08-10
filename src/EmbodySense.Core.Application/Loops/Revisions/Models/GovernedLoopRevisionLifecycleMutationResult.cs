using EmbodySense.Core.Common.Loops.Revisions.Models;

namespace EmbodySense.Core.Application.Loops.Revisions.Models;

/// <summary>Returns bounded exact evidence for one governed-loop revision lifecycle operation.</summary>
/// <param name="Status">The application outcome.</param>
/// <param name="OperationId">The exact operation identifier, or an empty value when no request was supplied.</param>
/// <param name="RequestHash">The server-computed canonical request hash, or an empty value when it could not be computed.</param>
/// <param name="Evidence">The durable terminal operation evidence when known.</param>
/// <param name="Head">The exact current lifecycle head when safely available.</param>
/// <param name="ValidationErrors">Bounded request validation errors.</param>
public sealed record GovernedLoopRevisionLifecycleMutationResult(
    GovernedLoopRevisionLifecycleMutationStatus Status,
    string OperationId,
    string RequestHash,
    GovernedLoopRevisionOperationEvidence? Evidence,
    GovernedLoopRevisionLifecycleHead? Head,
    IReadOnlyList<GovernedLoopRevisionLifecycleValidationError> ValidationErrors);
