using EmbodySense.Core.Common.Loops.Models.Custom.Graph;

namespace EmbodySense.Core.Common.CommandActions;

/// <summary>Derives exact schema-1 graph Action descriptors from immutable command-template hashes.</summary>
public static class CommandActionNodeDescriptors
{
    private const string Prefix = "command-";

    /// <summary>Creates the exact graph descriptor for one validated immutable template.</summary>
    public static GovernedLoopNodeDescriptor For(CommandActions.Models.CommandActionTemplate template)
    {
        ArgumentNullException.ThrowIfNull(template);
        if (CommandActionTemplateContract.Validate(template) is { } reasonCode)
        {
            throw new ArgumentException(reasonCode, nameof(template));
        }
        return new GovernedLoopNodeDescriptor(GovernedLoopNodeKind.Action, Prefix + template.ContentHash, 1);
    }

    /// <summary>Gets whether a descriptor is one canonical hash-pinned command Action.</summary>
    public static bool IsCommandAction(GovernedLoopNodeDescriptor? descriptor)
        => descriptor is
        {
            Kind: GovernedLoopNodeKind.Action,
            Version: 1,
            TypeId.Length: 72,
        }
        && descriptor.TypeId.StartsWith(Prefix, StringComparison.Ordinal)
        && CommandActionFingerprint.IsCanonicalSha256(descriptor.TypeId[Prefix.Length..]);

    /// <summary>Gets whether an exact descriptor is pinned to one validated template.</summary>
    public static bool Matches(GovernedLoopNodeDescriptor? descriptor, CommandActions.Models.CommandActionTemplate? template)
        => template is not null
            && CommandActionTemplateContract.Validate(template) is null
            && Equals(descriptor, For(template));
}
