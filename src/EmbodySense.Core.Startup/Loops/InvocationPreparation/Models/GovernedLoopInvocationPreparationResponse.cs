using EmbodySense.Core.Common.Loops.Revisions.Models;

namespace EmbodySense.Core.Startup.Loops.InvocationPreparation.Models;

/// <summary>Returns server-owned current-publication grant choices or one non-persisted least-authority preview.</summary>
/// <param name="Status">The closed preparation result.</param>
/// <param name="Publication">The exact current publication when it was safely established.</param>
/// <param name="EligibleGrants">Only currently eligible exact grants for that publication.</param>
/// <param name="Preview">The confirmation preview when no eligible grant exists.</param>
/// <param name="AsOfUtc">The trusted server evaluation time.</param>
/// <param name="ExpiresAtUtc">The server-owned current expiry projection, when applicable.</param>
/// <param name="Detail">A bounded non-sensitive explanation.</param>
public sealed record GovernedLoopInvocationPreparationResponse(
    GovernedLoopInvocationPreparationStatus Status,
    GovernedLoopRevisionPublicationPin? Publication,
    IReadOnlyList<GovernedLoopInvocationGrantChoice> EligibleGrants,
    GovernedLoopInvocationAuthorityPreview? Preview,
    DateTimeOffset AsOfUtc,
    DateTimeOffset? ExpiresAtUtc,
    string Detail)
{
    /// <summary>Gets a defensive immutable copy of server-authorized grant choices.</summary>
    public IReadOnlyList<GovernedLoopInvocationGrantChoice> EligibleGrants { get; } = EligibleGrants is null ? null! : Array.AsReadOnly(EligibleGrants.ToArray());
}
