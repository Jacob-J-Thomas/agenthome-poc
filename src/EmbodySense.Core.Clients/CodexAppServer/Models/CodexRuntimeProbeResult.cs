namespace EmbodySense.Core.Clients.CodexAppServer.Models;

internal sealed record CodexRuntimeProbeResult(bool IsUsable, string? Version, string Detail);
