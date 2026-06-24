namespace EmbodySense.Core.Startup.Configuration;

public sealed record WorkspaceConfigurationDocument(
    string Name,
    string Category,
    string Path,
    bool Exists,
    long SizeBytes,
    DateTimeOffset? LastModifiedUtc,
    string Content);
