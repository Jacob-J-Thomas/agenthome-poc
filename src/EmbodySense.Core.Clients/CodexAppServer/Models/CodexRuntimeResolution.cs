namespace EmbodySense.Core.Clients.CodexAppServer.Models;

public sealed record CodexRuntimeResolution(
    CodexRuntimeResolutionStatus Status,
    string? ExecutablePath,
    string? Version,
    string? ConfiguredModel,
    string? Source,
    string Detail);
