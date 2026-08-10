namespace EmbodySense.Core.Startup.ContextualRoles.Models;

/// <summary>Provides one stable value-free contextual-role inspection error.</summary>
/// <param name="Code">The stable machine-readable code.</param>
/// <param name="Detail">The bounded path-free and content-free explanation.</param>
public sealed record ContextualRoleError(string Code, string Detail);
