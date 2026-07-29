namespace EmbodySense.Core.Startup.Configuration.Models;

public sealed record WorkspaceConfigurationDocument(
    string Name,
    string Category,
    string Path,
    bool Exists,
    long SizeBytes,
    DateTimeOffset? LastModifiedUtc,
    string Content);
