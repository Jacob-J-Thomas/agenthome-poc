using EmbodySense.Core.Common.Loops.Models.Custom.Graph;

namespace EmbodySense.Core.Application.Loops.Sequential;

/// <summary>Defines the only exact node descriptors executable by the schema-1 sequential governed-loop lane.</summary>
public static class GovernedLoopSequentialNodeDescriptors
{
    /// <summary>Gets the exact supported manual-trigger descriptor.</summary>
    public static GovernedLoopNodeDescriptor ManualTrigger { get; } = new(GovernedLoopNodeKind.Trigger, "manual-trigger", 1);

    /// <summary>Gets the exact supported provider-inference descriptor.</summary>
    public static GovernedLoopNodeDescriptor ProviderInference { get; } = new(GovernedLoopNodeKind.Inference, "provider-inference", 1);

    /// <summary>Gets the exact supported successful-exit descriptor.</summary>
    public static GovernedLoopNodeDescriptor SuccessExit { get; } = new(GovernedLoopNodeKind.Exit, "success-exit", 1);

    /// <summary>Gets whether a descriptor exactly matches one supported kind, type identifier, and version.</summary>
    public static bool IsSupported(GovernedLoopNodeDescriptor? descriptor)
        => descriptor is not null
            && (Equals(descriptor, ManualTrigger)
                || Equals(descriptor, ProviderInference)
                || Equals(descriptor, SuccessExit));
}
