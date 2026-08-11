using EmbodySense.Core.Common.HumanInput.Models;

namespace EmbodySense.Core.Application.HumanInput.Lifecycle.Models;

/// <summary>Signals that one exact Human Input request intent is durably proved and may be offered to a later delivery adapter.</summary>
/// <param name="SchemaVersion">The opportunity schema version.</param>
/// <param name="OperationId">The exact proved operation identity.</param>
/// <param name="Request">The exact opaque immutable request reference.</param>
/// <param name="LifecycleVersion">The exact proved lifecycle version.</param>
/// <param name="ProvedAtUtc">The trusted UTC proof time.</param>
public sealed record HumanInputDeliveryOpportunity(
    int SchemaVersion,
    string OperationId,
    HumanInputRequestReference Request,
    long LifecycleVersion,
    DateTimeOffset ProvedAtUtc)
{
    /// <summary>Gets the only supported opportunity schema version.</summary>
    public const int CurrentSchemaVersion = 1;
}
