namespace EmbodySense.Core.Common.Governance.Permissions.Models;

/// <summary>
/// Represents a permission rule match.
/// </summary>
/// <param name="Entry">The entry.</param>
/// <param name="Specificity">The specificity.</param>
public sealed record PermissionRuleMatch(FileSystemPermissionEntry Entry, int Specificity);
