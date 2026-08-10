namespace EmbodySense.Core.Application.Loops.Revisions.Models;

/// <summary>Binds server-owned authority evaluation to one exact canonical lifecycle request.</summary>
/// <param name="Request">The exact lifecycle request.</param>
/// <param name="RequestHash">The server-computed canonical request hash.</param>
/// <param name="EvaluatedAtUtc">The trusted UTC evaluation instant.</param>
public sealed record GovernedLoopRevisionActorAuthorizationRequest(
    GovernedLoopRevisionLifecycleRequest Request,
    string RequestHash,
    DateTimeOffset EvaluatedAtUtc);
