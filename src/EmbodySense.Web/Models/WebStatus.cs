namespace EmbodySense.Web.Models;

/// <summary>
/// Projects the active local Web host and workspace status to a browser client.
/// </summary>
/// <param name="Client">The Web client name.</param>
/// <param name="PrimaryClient">Whether this client is the primary interface for the running host.</param>
/// <param name="WorkspaceRoot">The canonical workspace root.</param>
/// <param name="Initialized">Whether required workspace scaffolding exists.</param>
/// <param name="InitializationState">The authoritative <c>uninitialized</c>, <c>partial</c>, or <c>initialized</c> scaffold state.</param>
/// <param name="InitializationRequiresCleanup">Whether an unusable protected seed document must be cleaned up before initialization can succeed.</param>
/// <param name="InitializationOutcome">The latest explicit request outcome when this snapshot completes initialization; otherwise <see langword="null"/>.</param>
/// <param name="Url">The bound Web origin.</param>
/// <param name="CliRole">A short description of the CLI's complementary role.</param>
public sealed record WebStatus(
    string Client,
    bool PrimaryClient,
    string WorkspaceRoot,
    bool Initialized,
    string InitializationState,
    bool InitializationRequiresCleanup,
    string? InitializationOutcome,
    string Url,
    string CliRole);
