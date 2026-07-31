namespace EmbodySense.Core.Clients.CodexAppServer.Models;

/// <summary>
/// Captures the bounded version-probe outcome for one Codex executable candidate.
/// </summary>
/// <param name="IsUsable">Whether the probe started and returned an accepted version response.</param>
/// <param name="Version">The parsed version, or <see langword="null"/> when no usable version was reported.</param>
/// <param name="Detail">The bounded diagnostic explaining the probe outcome.</param>
internal sealed record CodexRuntimeProbeResult(bool IsUsable, string? Version, string Detail);
