namespace EmbodySense.Core.Startup.Runtime.Models;

public sealed record CodexRuntimeStatus(
    CodexRuntimeCompatibility Compatibility,
    string? RequestedExecutablePath,
    string? ResolvedExecutablePath,
    string? Version,
    string? ConfiguredModel,
    string? Source,
    string Detail);
