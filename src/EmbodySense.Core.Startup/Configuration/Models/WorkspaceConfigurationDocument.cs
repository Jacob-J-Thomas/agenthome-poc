namespace EmbodySense.Core.Startup.Configuration.Models;

/// <summary>
/// Provides a bounded, display-oriented projection of one startup document.
/// </summary>
/// <param name="Name">The document display name.</param>
/// <param name="Category">The document display category.</param>
/// <param name="Path">The resolved source path.</param>
/// <param name="Exists">Whether the existence probe observed the file.</param>
/// <param name="SizeBytes">The source length observed before reading, or zero when absent.</param>
/// <param name="LastModifiedUtc">The source modification time observed before reading, or null when absent.</param>
/// <param name="Content">
/// Bounded content with likely secret-bearing lines redacted, a truncation marker when needed, or
/// an explanatory message for a handled I/O or access failure.
/// </param>
public sealed record WorkspaceConfigurationDocument(
    string Name,
    string Category,
    string Path,
    bool Exists,
    long SizeBytes,
    DateTimeOffset? LastModifiedUtc,
    string Content);
