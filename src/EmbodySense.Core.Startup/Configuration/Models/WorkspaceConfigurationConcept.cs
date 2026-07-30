namespace EmbodySense.Core.Startup.Configuration.Models;

/// <summary>
/// Describes the observed presence of one user-facing workspace concept.
/// </summary>
/// <param name="Name">The display name.</param>
/// <param name="Category">The display category.</param>
/// <param name="Status"><c>Present</c> or <c>Missing</c> according to the concept's file-system probe.</param>
/// <param name="Detail">A concise description of the concept's current contract.</param>
public sealed record WorkspaceConfigurationConcept(
    string Name,
    string Category,
    string Status,
    string Detail);
