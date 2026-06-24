namespace EmbodySense.Web.Models;

public sealed record WebStatus(
    string Client,
    bool PrimaryClient,
    string WorkspaceRoot,
    bool Initialized,
    string Url,
    string CliRole);
