using EmbodySense.Core.Common.Capabilities;
using EmbodySense.Core.Common.Capabilities.Models;

namespace EmbodySense.Core.Startup.Capabilities;

/// <summary>Defines safe descriptors for implementations shipped in the local runtime.</summary>
public static class BuiltInCapabilityCatalog
{
    /// <summary>Gets the built-ins available to workspace bootstrap; descriptors never assign or authorize capability use.</summary>
    public static IReadOnlyList<CapabilityDescriptor> Descriptors { get; } = Array.AsReadOnly(new[]
    {
        Create("org.embodysense/conversation-turn", "conversation-turn", CapabilityKind.GraphNode, CapabilitySideEffectClass.None, "Execute one governed default-conversation inference step."),
        Create("org.embodysense/model-inference", "model-inference", CapabilityKind.GraphNode, CapabilitySideEffectClass.None, "Dispatch one admitted model-inference node through the governed local runtime."),
        Create("org.embodysense/workspace-command", "workspace-command", CapabilityKind.Actuator, CapabilitySideEffectClass.LocalReversible, "Expose governed workspace commands through the runtime tool broker.")
    });

    private static CapabilityDescriptor Create(string idValue, string implementationId, CapabilityKind kind, CapabilitySideEffectClass sideEffectClass, string purpose)
    {
        _ = CapabilityId.TryParse(idValue, out var id, out _);
        _ = CapabilityProviderId.TryParse("org.embodysense", out var provider, out _);
        _ = CapabilityVersion.TryParse("1.0.0", out var version, out _);
        _ = CapabilityVersionRange.TryParse("*", out var hostRange, out _);
        _ = CapabilityJsonSchema.TryCreate($"{{\"$schema\":\"{CapabilityJsonSchema.Draft202012Dialect}\",\"type\":\"object\"}}", out var schema, out _);
        return new CapabilityDescriptor(
            CapabilityDescriptor.CurrentSchemaVersion,
            id!,
            kind,
            version!,
            new CapabilityImplementationIdentity(provider!, implementationId),
            new CapabilityProvenance(CapabilityProvenanceKind.BuiltIn, $"https://embodysense.dev/builtins/{implementationId}", "1", null),
            new CapabilityCompatibility(hostRange!, [CapabilityPlatform.Any]),
            purpose,
            schema!,
            schema!,
            new CapabilityResourceLimits(86_400_000, 1_099_511_627_776, 16_777_216, 1_024),
            sideEffectClass,
            new CapabilityAccessRequirements([], CapabilityEgressMode.None, [], []));
    }
}
