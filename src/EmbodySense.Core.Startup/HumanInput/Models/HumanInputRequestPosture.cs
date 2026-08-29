using EmbodySense.Core.Common.HumanInput.Lifecycle.Models;
using EmbodySense.Core.Common.HumanInput.Models;

namespace EmbodySense.Core.Startup.HumanInput.Models;

/// <summary>Projects one canonical Human Input lifecycle and response posture with private routing, actor, role, grant, and authority evidence redacted.</summary>
/// <param name="SchemaVersion">The schema version of the projected lifecycle.</param>
/// <param name="RequestId">The stable request identity.</param>
/// <param name="LifecycleVersion">The exact optimistic lifecycle version.</param>
/// <param name="Status">The current closed lifecycle posture.</param>
/// <param name="CurrentRequest">The exact current immutable request reference.</param>
/// <param name="Presentation">The display-safe current request contract.</param>
/// <param name="ReminderCount">The committed delivery-opportunity count.</param>
/// <param name="SupersedesRequestId">The earlier request replaced by this request, when present.</param>
/// <param name="SupersededByRequestId">The later request that replaced this request, when terminal.</param>
/// <param name="UpdatedAtUtc">The trusted lifecycle update instant.</param>
/// <param name="AcceptedResponseCount">The number of retained valid response artifacts for the current request version.</param>
/// <param name="ActiveResponseCount">The number of non-withdrawn retained response artifacts.</param>
/// <param name="WithdrawnResponseCount">The number of retained responses no longer active.</param>
/// <param name="IsAnswered">Whether one canonical selection has answered the request; the selected value and response references are redacted.</param>
/// <param name="LatestConflict">The newest retained value-free operation conflict, or null when none exists.</param>
public sealed record HumanInputRequestPosture(
    int SchemaVersion,
    string RequestId,
    long LifecycleVersion,
    HumanInputRequestLifecycleStatus Status,
    HumanInputRequestReference CurrentRequest,
    HumanInputRequestPresentation Presentation,
    int ReminderCount,
    string? SupersedesRequestId,
    string? SupersededByRequestId,
    DateTimeOffset UpdatedAtUtc,
    int AcceptedResponseCount,
    int ActiveResponseCount,
    int WithdrawnResponseCount,
    bool IsAnswered,
    HumanInputRequestConflict? LatestConflict);
