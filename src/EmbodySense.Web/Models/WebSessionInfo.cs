namespace EmbodySense.Web.Models;

/// <summary>
/// Carries the bearer token issued to the bootstrapping local browser session.
/// </summary>
/// <param name="Token">The opaque session token required by authenticated HTTP and SignalR surfaces.</param>
/// <param name="ChatRequestScope">A non-secret process-session scope for bounded browser chat-request reconciliation state.</param>
public sealed record WebSessionInfo(string Token, string ChatRequestScope);
