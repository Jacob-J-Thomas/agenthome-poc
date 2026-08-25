using EmbodySense.Core.Common.Authority.Grants.Models;

namespace EmbodySense.Core.Startup.Loops.InvocationPreparation.Models;

/// <summary>Returns the exact grant that a confirmed invocation may pass to canonical admission.</summary>
/// <param name="Status">The closed confirmation result.</param>
/// <param name="Grant">The exact durable authority-grant reference when confirmation succeeded.</param>
/// <param name="AsOfUtc">The trusted server time at which the result was projected.</param>
/// <param name="Detail">A bounded non-sensitive explanation.</param>
public sealed record GovernedLoopInvocationAuthorityConfirmationResult(
    GovernedLoopInvocationAuthorityConfirmationStatus Status,
    AuthorityGrantReference? Grant,
    DateTimeOffset AsOfUtc,
    string Detail);
