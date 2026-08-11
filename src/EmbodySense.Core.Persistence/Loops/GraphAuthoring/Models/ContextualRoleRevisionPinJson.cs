namespace EmbodySense.Core.Persistence.Loops.GraphAuthoring.Models;

internal sealed record ContextualRoleRevisionPinJson(
    string? ContentHash,
    int Revision,
    string? RoleId);
