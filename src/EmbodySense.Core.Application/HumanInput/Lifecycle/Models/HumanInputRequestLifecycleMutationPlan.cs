using EmbodySense.Core.Common.HumanInput.Lifecycle.Models;
using EmbodySense.Core.Common.HumanInput.Models;

namespace EmbodySense.Core.Application.HumanInput.Lifecycle.Models;

internal sealed record HumanInputRequestLifecycleMutationPlan(
    HumanInputRequestLifecycleMutationStatus Status,
    HumanInputRequestLifecycleOperationOutcome Outcome,
    HumanInputRequestLifecycleOperationFailureCode FailureCode,
    HumanInputRequestLifecycleHead? PreviousHead,
    HumanInputRequestLifecycleHead? ResultHead,
    string? RelatedRequestId,
    HumanInputRequestLifecycleHead? RelatedPreviousHead,
    HumanInputRequestLifecycleHead? RelatedResultHead,
    HumanInputRequest? PreviousRequest,
    HumanInputRequest? CandidateRequest,
    HumanInputRequest? RequestToAppend,
    bool CanPersist);
