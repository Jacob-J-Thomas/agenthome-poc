namespace EmbodySense.Core.Startup.HumanInput.Models;

/// <summary>Carries the detached immutable Human Input request reference supplied by a surface.</summary>
/// <param name="RequestId">The exact request identity.</param>
/// <param name="RequestVersionId">The exact immutable request-version identity.</param>
/// <param name="RequestHash">The exact canonical request hash.</param>
public sealed record HumanInputSurfaceRequestReference(string RequestId, string RequestVersionId, string RequestHash);
