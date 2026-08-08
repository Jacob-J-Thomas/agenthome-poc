namespace EmbodySense.Web.Models;

/// <summary>
/// Describes the process generation and workspace scope established for a local browser session.
/// </summary>
/// <param name="GenerationId">A non-secret identifier that changes whenever the Web host process restarts.</param>
/// <param name="ChatRequestScope">A non-secret workspace scope for bounded browser chat-request reconciliation state.</param>
public sealed record WebSessionInfo(string GenerationId, string ChatRequestScope);
