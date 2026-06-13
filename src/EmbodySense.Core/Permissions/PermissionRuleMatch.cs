namespace EmbodySense.Core.Permissions;

internal sealed record PermissionRuleMatch(FileSystemPermissionEntry Entry, int Specificity);
