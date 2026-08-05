namespace EmbodySense.Web.Models;

/// <summary>
/// Describes the process generation established for a local browser session.
/// </summary>
/// <param name="GenerationId">A non-secret identifier that changes whenever the Web host process restarts.</param>
public sealed record WebSessionInfo(string GenerationId);
