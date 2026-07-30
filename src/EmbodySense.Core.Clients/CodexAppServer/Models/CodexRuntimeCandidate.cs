namespace EmbodySense.Core.Clients.CodexAppServer.Models;

/// <summary>
/// Identifies one Codex executable path and the discovery source that supplied it.
/// </summary>
/// <param name="ExecutablePath">The absolute or command-resolvable executable candidate.</param>
/// <param name="Source">The human-readable discovery source used in diagnostics.</param>
internal sealed record CodexRuntimeCandidate(string ExecutablePath, string Source);
