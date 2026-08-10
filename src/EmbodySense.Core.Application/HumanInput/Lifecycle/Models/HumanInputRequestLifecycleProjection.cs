using EmbodySense.Core.Common.HumanInput.Lifecycle.Models;
using EmbodySense.Core.Common.HumanInput.Models;

namespace EmbodySense.Core.Application.HumanInput.Lifecycle.Models;

/// <summary>Projects one privacy-safe Human Input request lifecycle without prompt, routing, binding, actor, or reason data.</summary>
/// <param name="SchemaVersion">The lifecycle schema version.</param>
/// <param name="RequestId">The stable request identifier.</param>
/// <param name="LifecycleVersion">The optimistic lifecycle version.</param>
/// <param name="Status">The current closed lifecycle posture.</param>
/// <param name="CurrentRequest">The exact opaque immutable request reference.</param>
/// <param name="ReminderCount">The bounded reminder count.</param>
/// <param name="SupersedesRequestId">The earlier request replaced by this request, when present.</param>
/// <param name="SupersededByRequestId">The later request that replaced this request, when terminal.</param>
/// <param name="UpdatedAtUtc">The trusted UTC lifecycle update time.</param>
public sealed record HumanInputRequestLifecycleProjection(
    int SchemaVersion,
    string RequestId,
    long LifecycleVersion,
    HumanInputRequestLifecycleStatus Status,
    HumanInputRequestReference CurrentRequest,
    int ReminderCount,
    string? SupersedesRequestId,
    string? SupersededByRequestId,
    DateTimeOffset UpdatedAtUtc);
