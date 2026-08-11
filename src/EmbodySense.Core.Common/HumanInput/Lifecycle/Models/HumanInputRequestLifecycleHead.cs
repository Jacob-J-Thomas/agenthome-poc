using EmbodySense.Core.Common.HumanInput.Models;

namespace EmbodySense.Core.Common.HumanInput.Lifecycle.Models;

/// <summary>Projects the exact optimistic head of one durable Human Input request lifecycle.</summary>
/// <param name="SchemaVersion">The lifecycle-head schema version.</param>
/// <param name="RequestId">The stable request identifier.</param>
/// <param name="LifecycleVersion">The positive optimistic lifecycle version.</param>
/// <param name="Status">The current closed lifecycle posture.</param>
/// <param name="CurrentRequest">The exact current immutable request version.</param>
/// <param name="ReminderCount">The number of committed reminder opportunities.</param>
/// <param name="SupersedesRequestId">The earlier request replaced by this request, when one exists.</param>
/// <param name="SupersededByRequestId">The later request that replaced this request, when terminally superseded.</param>
/// <param name="LastOperationId">The exact operation that produced this projection.</param>
/// <param name="UpdatedAtUtc">The trusted UTC projection time.</param>
public sealed record HumanInputRequestLifecycleHead(
    int SchemaVersion,
    string RequestId,
    long LifecycleVersion,
    HumanInputRequestLifecycleStatus Status,
    HumanInputRequestReference CurrentRequest,
    int ReminderCount,
    string? SupersedesRequestId,
    string? SupersededByRequestId,
    string LastOperationId,
    DateTimeOffset UpdatedAtUtc);
