namespace EmbodySense.Cli.Permissions;

internal sealed record PermissionRuleMatch(FileSystemPermissionEntry Entry, int Specificity);
