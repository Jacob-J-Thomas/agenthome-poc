namespace EmbodySense.Core.Startup.Runtime.Models;

/// <summary>
/// Provides an interface-safe role and content projection of one runtime transcript message.
/// </summary>
/// <param name="Role">The normalized lowercase conversation role.</param>
/// <param name="Content">The user-visible message content.</param>
public sealed record AgentRuntimeTranscriptMessage(string Role, string Content);
