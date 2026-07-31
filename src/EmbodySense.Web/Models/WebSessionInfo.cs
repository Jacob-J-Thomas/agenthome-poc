namespace EmbodySense.Web.Models;

/// <summary>
/// Carries the bearer token issued to the bootstrapping local browser session.
/// </summary>
/// <param name="Token">The opaque session token required by authenticated HTTP and SignalR surfaces.</param>
public sealed record WebSessionInfo(string Token);
