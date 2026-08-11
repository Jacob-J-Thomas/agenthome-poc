using EmbodySense.Core.Common.HumanInput.Lifecycle.Models;
using EmbodySense.Core.Common.HumanInput.Responses.Models;

namespace EmbodySense.Core.Application.HumanInput.Responses.Models;

/// <summary>Projects privacy-safe response posture without values, actors, roles, routing, or exact binding data.</summary>
/// <param name="SchemaVersion">The projection schema version.</param>
/// <param name="RequestId">The stable request identity.</param>
/// <param name="RequestVersionId">The exact immutable request version.</param>
/// <param name="LifecycleVersion">The optimistic request lifecycle version.</param>
/// <param name="LifecycleStatus">The current request lifecycle status.</param>
/// <param name="AcceptedResponseCount">The number of retained valid response artifacts for this request version.</param>
/// <param name="ActiveResponseCount">The number of currently active, non-withdrawn responses.</param>
/// <param name="WithdrawnResponseCount">The number of committed withdrawals.</param>
/// <param name="AnswerSelection">The opaque durable selection reference when answered.</param>
/// <param name="UpdatedAtUtc">The trusted UTC request-head update time.</param>
public sealed record HumanInputResponseLifecycleProjection(
    int SchemaVersion,
    string RequestId,
    string RequestVersionId,
    long LifecycleVersion,
    HumanInputRequestLifecycleStatus LifecycleStatus,
    int AcceptedResponseCount,
    int ActiveResponseCount,
    int WithdrawnResponseCount,
    HumanInputResponseSelectionReference? AnswerSelection,
    DateTimeOffset UpdatedAtUtc);
