namespace EmbodySense.Core.Startup.Runtime.Models;

/// <summary>
/// Reports the resolved Codex executable and configured-model compatibility observed by startup.
/// </summary>
/// <param name="Compatibility">The fail-closed compatibility classification.</param>
/// <param name="RequestedExecutablePath">The caller's explicit executable request, when supplied.</param>
/// <param name="ResolvedExecutablePath">The executable that passed path resolution, when one did.</param>
/// <param name="Version">The probed Codex version text, when available.</param>
/// <param name="ConfiguredModel">The configured model whose availability was checked.</param>
/// <param name="Source">How the resolved executable candidate was discovered.</param>
/// <param name="Detail">An interface-ready diagnostic with recovery guidance.</param>
public sealed record CodexRuntimeStatus(
    CodexRuntimeCompatibility Compatibility,
    string? RequestedExecutablePath,
    string? ResolvedExecutablePath,
    string? Version,
    string? ConfiguredModel,
    string? Source,
    string Detail);
