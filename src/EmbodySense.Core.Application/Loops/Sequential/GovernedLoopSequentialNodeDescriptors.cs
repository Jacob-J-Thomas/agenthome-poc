using EmbodySense.Core.Common.Loops.Models.Custom.Graph;
using EmbodySense.Core.Common.Loops.PureNodes;

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

    /// <summary>Gets the exact supported identity Transform descriptor.</summary>
    public static GovernedLoopNodeDescriptor IdentityTransform { get; } = Pure(GovernedLoopNodeKind.Transform, GovernedLoopPureNodeVocabulary.IdentityTransform);

    /// <summary>Gets the exact supported structured-selection Transform descriptor.</summary>
    public static GovernedLoopNodeDescriptor StructuredSelect { get; } = Pure(GovernedLoopNodeKind.Transform, GovernedLoopPureNodeVocabulary.StructuredSelect);

    /// <summary>Gets the exact supported ordered-text-concatenation Transform descriptor.</summary>
    public static GovernedLoopNodeDescriptor OrderedTextConcat { get; } = Pure(GovernedLoopNodeKind.Transform, GovernedLoopPureNodeVocabulary.OrderedTextConcat);

    /// <summary>Gets the exact supported schema-conformance Validate descriptor.</summary>
    public static GovernedLoopNodeDescriptor SchemaConformance { get; } = Pure(GovernedLoopNodeKind.Validate, GovernedLoopPureNodeVocabulary.SchemaConformance);

    /// <summary>Gets the exact supported canonical-equality Validate descriptor.</summary>
    public static GovernedLoopNodeDescriptor CanonicalEquality { get; } = Pure(GovernedLoopNodeKind.Validate, GovernedLoopPureNodeVocabulary.CanonicalEquality);

    /// <summary>Gets the exact supported inclusive-integer-range Validate descriptor.</summary>
    public static GovernedLoopNodeDescriptor InclusiveIntegerRange { get; } = Pure(GovernedLoopNodeKind.Validate, GovernedLoopPureNodeVocabulary.InclusiveIntegerRange);

    /// <summary>Gets the exact supported inclusive-number-range Validate descriptor.</summary>
    public static GovernedLoopNodeDescriptor InclusiveNumberRange { get; } = Pure(GovernedLoopNodeKind.Validate, GovernedLoopPureNodeVocabulary.InclusiveNumberRange);

    /// <summary>Gets the exact supported text-length Validate descriptor.</summary>
    public static GovernedLoopNodeDescriptor TextLength { get; } = Pure(GovernedLoopNodeKind.Validate, GovernedLoopPureNodeVocabulary.TextLength);

    /// <summary>Gets the exact supported array-length Validate descriptor.</summary>
    public static GovernedLoopNodeDescriptor ArrayLength { get; } = Pure(GovernedLoopNodeKind.Validate, GovernedLoopPureNodeVocabulary.ArrayLength);

    /// <summary>Gets whether a descriptor exactly matches one supported kind, type identifier, and version.</summary>
    public static bool IsSupported(GovernedLoopNodeDescriptor? descriptor)
        => descriptor is not null
            && (Equals(descriptor, ManualTrigger)
                || Equals(descriptor, ProviderInference)
                || Equals(descriptor, SuccessExit)
                || IsPure(descriptor));

    /// <summary>Gets whether a descriptor exactly names one supported dependency-free Transform or Validate.</summary>
    public static bool IsPure(GovernedLoopNodeDescriptor? descriptor)
        => IsTransform(descriptor) || IsValidate(descriptor);

    /// <summary>Gets whether a descriptor exactly names one supported dependency-free Transform.</summary>
    public static bool IsTransform(GovernedLoopNodeDescriptor? descriptor)
        => descriptor is not null
            && descriptor.Kind == GovernedLoopNodeKind.Transform
            && descriptor.Version == GovernedLoopPureNodeVocabulary.DescriptorVersion
            && GovernedLoopPureNodeVocabulary.IsTransform(descriptor.TypeId);

    /// <summary>Gets whether a descriptor exactly names one supported dependency-free Validate.</summary>
    public static bool IsValidate(GovernedLoopNodeDescriptor? descriptor)
        => descriptor is not null
            && descriptor.Kind == GovernedLoopNodeKind.Validate
            && descriptor.Version == GovernedLoopPureNodeVocabulary.DescriptorVersion
            && GovernedLoopPureNodeVocabulary.IsValidate(descriptor.TypeId);

    private static GovernedLoopNodeDescriptor Pure(GovernedLoopNodeKind kind, string typeId)
        => new(kind, typeId, GovernedLoopPureNodeVocabulary.DescriptorVersion);
}
