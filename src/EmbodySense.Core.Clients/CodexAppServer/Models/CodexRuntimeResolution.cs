namespace EmbodySense.Core.Clients.CodexAppServer.Models;

/// <summary>
/// Captures the bounded discovery and compatibility-probe result for one selected Codex runtime.
/// </summary>
/// <param name="Status">The terminal compatibility classification.</param>
/// <param name="ExecutablePath">The selected or most relevant candidate path, or <see langword="null"/> when none was discovered.</param>
/// <param name="Version">The probed runtime version, or <see langword="null"/> when unavailable.</param>
/// <param name="ConfiguredModel">The model required by the probe, or <see langword="null"/> for external selection.</param>
/// <param name="Source">The candidate source such as an explicit path, Codex Desktop, or <c>PATH</c>.</param>
/// <param name="Detail">The bounded human-readable diagnostic.</param>
public sealed record CodexRuntimeResolution(
    CodexRuntimeResolutionStatus Status,
    string? ExecutablePath,
    string? Version,
    string? ConfiguredModel,
    string? Source,
    string Detail);
