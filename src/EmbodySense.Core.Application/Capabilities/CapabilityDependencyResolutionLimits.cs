namespace EmbodySense.Core.Application.Capabilities;

/// <summary>Defines bounded schema-version-1 traversal limits for dependency resolution.</summary>
public sealed record CapabilityDependencyResolutionLimits(int MaximumDepth, int MaximumDependencies, int MaximumCandidates)
{
    /// <summary>Gets the conservative schema-version-1 defaults.</summary>
    public static CapabilityDependencyResolutionLimits Default { get; } = new(16, 256, 1_024);

    /// <summary>Validates the supplied limits.</summary>
    public bool IsValid => MaximumDepth is >= 1 and <= 64 && MaximumDependencies is >= 1 and <= 4_096 && MaximumCandidates is >= 1 and <= 16_384;
}
