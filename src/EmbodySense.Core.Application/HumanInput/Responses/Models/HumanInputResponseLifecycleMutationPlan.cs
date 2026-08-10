using System.Collections.Immutable;
using EmbodySense.Core.Common.HumanInput.Lifecycle.Models;
using EmbodySense.Core.Common.HumanInput.Responses.Models;

namespace EmbodySense.Core.Application.HumanInput.Responses.Models;

internal sealed record HumanInputResponseLifecycleMutationPlan(
    HumanInputResponseLifecycleMutationStatus Status,
    HumanInputResponseOperationOutcome Outcome,
    HumanInputResponseOperationFailureCode FailureCode,
    HumanInputRequestLifecycleHead? PreviousHead,
    HumanInputRequestLifecycleHead? ResultHead,
    HumanInputResponseArtifact? ResponseToAppend,
    ImmutableArray<HumanInputResponseReference> TargetResponses,
    HumanInputResponseSelection? SelectionToAppend,
    bool CanPersist);
