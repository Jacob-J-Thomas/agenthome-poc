namespace EmbodySense.Core.Application.Triggers.Models;

/// <summary>Captures exact generation-scoped worker ownership.</summary>
/// <param name="WorkerId">The bounded composition-owned worker identity.</param>
/// <param name="Generation">The monotonically increasing entry ownership generation.</param>
/// <param name="AcquiredAtUtc">The first acquisition instant for this generation.</param>
/// <param name="ExpiresAtUtc">The exclusive ownership expiry instant.</param>
/// <param name="RenewalCount">The bounded successful renewal count.</param>
/// <param name="ReleasedAtUtc">The explicit release instant, or <see langword="null"/> while ownership is live.</param>
public sealed record TriggerWorkerLease(string WorkerId, long Generation, DateTimeOffset AcquiredAtUtc, DateTimeOffset ExpiresAtUtc, int RenewalCount, DateTimeOffset? ReleasedAtUtc = null);
