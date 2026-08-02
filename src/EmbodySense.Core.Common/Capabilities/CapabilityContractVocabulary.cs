namespace EmbodySense.Core.Common.Capabilities;

internal static class CapabilityContractVocabulary
{
    internal static string ToCanonical(CapabilityKind value) => value switch
    {
        CapabilityKind.TriggerAdapter => "trigger-adapter",
        CapabilityKind.GraphNode => "graph-node",
        CapabilityKind.Actuator => "actuator",
        CapabilityKind.ContextSource => "context-source",
        CapabilityKind.ModelProfile => "model-profile",
        CapabilityKind.ObservationSource => "observation-source",
        CapabilityKind.Evaluation => "evaluation",
        CapabilityKind.Skill => "skill",
        CapabilityKind.Hook => "hook",
        CapabilityKind.SurfaceAdapter => "surface-adapter",
        _ => "unknown"
    };

    internal static string ToCanonical(CapabilityProvenanceKind value) => value switch
    {
        CapabilityProvenanceKind.BuiltIn => "built-in",
        CapabilityProvenanceKind.LocalSource => "local-source",
        CapabilityProvenanceKind.Package => "package",
        CapabilityProvenanceKind.RemoteArtifact => "remote-artifact",
        _ => "unknown"
    };

    internal static string ToCanonical(CapabilitySideEffectClass value) => value switch
    {
        CapabilitySideEffectClass.None => "none",
        CapabilitySideEffectClass.ReadOnly => "read-only",
        CapabilitySideEffectClass.LocalReversible => "local-reversible",
        CapabilitySideEffectClass.ExternalReversible => "external-reversible",
        CapabilitySideEffectClass.Irreversible => "irreversible",
        _ => "unknown"
    };

    internal static string ToCanonical(CapabilityEgressMode value) => value switch
    {
        CapabilityEgressMode.None => "none",
        CapabilityEgressMode.Restricted => "restricted",
        CapabilityEgressMode.Unrestricted => "unrestricted",
        _ => "unknown"
    };

    internal static bool TryParse(string value, out CapabilityKind parsed)
    {
        parsed = value switch
        {
            "trigger-adapter" => CapabilityKind.TriggerAdapter,
            "graph-node" => CapabilityKind.GraphNode,
            "actuator" => CapabilityKind.Actuator,
            "context-source" => CapabilityKind.ContextSource,
            "model-profile" => CapabilityKind.ModelProfile,
            "observation-source" => CapabilityKind.ObservationSource,
            "evaluation" => CapabilityKind.Evaluation,
            "skill" => CapabilityKind.Skill,
            "hook" => CapabilityKind.Hook,
            "surface-adapter" => CapabilityKind.SurfaceAdapter,
            _ => CapabilityKind.Unknown
        };
        return parsed != CapabilityKind.Unknown;
    }

    internal static bool TryParse(string value, out CapabilityProvenanceKind parsed)
    {
        parsed = value switch
        {
            "built-in" => CapabilityProvenanceKind.BuiltIn,
            "local-source" => CapabilityProvenanceKind.LocalSource,
            "package" => CapabilityProvenanceKind.Package,
            "remote-artifact" => CapabilityProvenanceKind.RemoteArtifact,
            _ => CapabilityProvenanceKind.Unknown
        };
        return parsed != CapabilityProvenanceKind.Unknown;
    }

    internal static bool TryParse(string value, out CapabilitySideEffectClass parsed)
    {
        parsed = value switch
        {
            "none" => CapabilitySideEffectClass.None,
            "read-only" => CapabilitySideEffectClass.ReadOnly,
            "local-reversible" => CapabilitySideEffectClass.LocalReversible,
            "external-reversible" => CapabilitySideEffectClass.ExternalReversible,
            "irreversible" => CapabilitySideEffectClass.Irreversible,
            _ => CapabilitySideEffectClass.Unknown
        };
        return parsed != CapabilitySideEffectClass.Unknown;
    }

    internal static bool TryParse(string value, out CapabilityEgressMode parsed)
    {
        parsed = value switch
        {
            "none" => CapabilityEgressMode.None,
            "restricted" => CapabilityEgressMode.Restricted,
            "unrestricted" => CapabilityEgressMode.Unrestricted,
            _ => CapabilityEgressMode.Unknown
        };
        return parsed != CapabilityEgressMode.Unknown;
    }
}
