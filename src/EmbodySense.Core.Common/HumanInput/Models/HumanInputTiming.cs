namespace EmbodySense.Core.Common.HumanInput.Models;

/// <summary>
/// Defines the finite UTC window in which a response can be submitted.
/// </summary>
/// <param name="RequestedAtUtc">The UTC request creation time.</param>
/// <param name="ExpiresAtUtc">The UTC response deadline.</param>
public sealed record HumanInputTiming(DateTimeOffset RequestedAtUtc, DateTimeOffset ExpiresAtUtc);
