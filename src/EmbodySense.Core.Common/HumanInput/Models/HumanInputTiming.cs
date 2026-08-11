namespace EmbodySense.Core.Common.HumanInput.Models;

/// <summary>
/// Defines the finite UTC window in which a response can be submitted.
/// </summary>
/// <param name="RequestedAtUtc">The inclusive UTC opening of the response window selected in exact request intent.</param>
/// <param name="ExpiresAtUtc">The UTC response deadline.</param>
public sealed record HumanInputTiming(DateTimeOffset RequestedAtUtc, DateTimeOffset ExpiresAtUtc);
