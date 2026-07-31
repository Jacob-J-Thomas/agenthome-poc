namespace EmbodySense.Web.Models;

/// <summary>
/// Projects the active local Web host and workspace status to a browser client.
/// </summary>
/// <param name="Client">The Web client name.</param>
/// <param name="PrimaryClient">Whether this client is the primary interface for the running host.</param>
/// <param name="WorkspaceRoot">The canonical workspace root.</param>
/// <param name="Initialized">Whether required workspace scaffolding exists.</param>
/// <param name="Url">The bound Web origin.</param>
/// <param name="CliRole">A short description of the CLI's complementary role.</param>
public sealed record WebStatus(
    string Client,
    bool PrimaryClient,
    string WorkspaceRoot,
    bool Initialized,
    string Url,
    string CliRole);
