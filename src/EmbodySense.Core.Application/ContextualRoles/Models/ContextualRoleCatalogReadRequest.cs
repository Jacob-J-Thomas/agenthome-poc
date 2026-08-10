namespace EmbodySense.Core.Application.ContextualRoles.Models;

/// <summary>Requests one deterministic bounded contextual-role catalog page.</summary>
/// <param name="StartAfterRoleId">The optional exclusive stable-role cursor.</param>
/// <param name="MaximumCount">The requested page size.</param>
public sealed record ContextualRoleCatalogReadRequest(string? StartAfterRoleId, int MaximumCount);
