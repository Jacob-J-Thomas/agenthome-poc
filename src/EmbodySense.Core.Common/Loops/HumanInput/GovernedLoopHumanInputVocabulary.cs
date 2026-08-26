using EmbodySense.Core.Common.Loops.Models.Custom.Graph;

namespace EmbodySense.Core.Common.Loops.HumanInput;

/// <summary>Defines the one exact schema-1 descriptor and port vocabulary for untrusted Human Input graph nodes.</summary>
public static class GovernedLoopHumanInputVocabulary
{
    /// <summary>Gets the only supported Human Input descriptor type identifier.</summary>
    public const string TypeId = "human-input";

    /// <summary>Gets the only supported Human Input descriptor version.</summary>
    public const int DescriptorVersion = 1;

    /// <summary>Gets the sole output port carrying an untrusted typed response.</summary>
    public const string ResponsePortId = "response";

    /// <summary>Gets whether a descriptor exactly identifies the supported schema-1 Human Input node.</summary>
    /// <param name="descriptor">The descriptor to inspect.</param>
    /// <returns><see langword="true"/> only for the exact Human Input kind, type ID, and version.</returns>
    public static bool IsSupported(GovernedLoopNodeDescriptor? descriptor)
        => descriptor is not null
            && descriptor.Kind == GovernedLoopNodeKind.HumanInput
            && descriptor.Version == DescriptorVersion
            && string.Equals(descriptor.TypeId, TypeId, StringComparison.Ordinal);
}
