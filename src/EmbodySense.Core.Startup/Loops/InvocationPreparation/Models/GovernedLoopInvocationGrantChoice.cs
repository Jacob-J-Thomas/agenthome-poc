using EmbodySense.Core.Common.Authority.Grants.Models;

namespace EmbodySense.Core.Startup.Loops.InvocationPreparation.Models;

/// <summary>Projects one server-authorized exact authority grant choice for the selected current publication.</summary>
/// <param name="Grant">The exact immutable grant reference, which canonical admission revalidates before use.</param>
/// <param name="ExpiresAtUtc">The current server-owned expiration boundary, when the exact grant has one.</param>
public sealed record GovernedLoopInvocationGrantChoice(AuthorityGrantReference Grant, DateTimeOffset? ExpiresAtUtc);
