using EmbodySense.Core.Common.Capabilities;
using EmbodySense.Core.Common.Capabilities.Models;

namespace EmbodySense.Core.Common.CommandActions.Models;

/// <summary>Declares one immutable server-owned structured process template without paths or secret values.</summary>
/// <param name="SchemaVersion">The schema version, which must be 1.</param>
/// <param name="Capability">The exact actuator descriptor identity.</param>
/// <param name="Implementation">The exact registered implementation identity.</param>
/// <param name="ArtifactDigest">The exact immutable executable artifact digest.</param>
/// <param name="ActivationRevision">The exact active artifact lifecycle revision.</param>
/// <param name="TemplateId">The stable template identity.</param>
/// <param name="TemplateVersion">The positive immutable template version.</param>
/// <param name="Slots">The name-ordered typed slot declarations.</param>
/// <param name="Arguments">The ordered complete argument-token declarations.</param>
/// <param name="Environment">The name-ordered fixed non-secret child environment.</param>
/// <param name="SecondaryGrammar">The server-owned attestation that supplied argument tokens cannot enter a response, script, configuration, or other secondary grammar.</param>
/// <param name="StandardInput">The non-interactive standard-input posture.</param>
/// <param name="StandardInputSlot">The exact input slot when standard input is not closed.</param>
/// <param name="Output">The exact structured standard-output contract.</param>
/// <param name="Isolation">The controls that must be effective before launch.</param>
/// <param name="RequiresCredentialChannel">Whether the template stays unavailable until the shared one-shot credential channel exists.</param>
/// <param name="ContentHash">The canonical lowercase SHA-256 hash over every preceding field.</param>
public sealed record CommandActionTemplate(
    int SchemaVersion,
    CapabilityDescriptorIdentity Capability,
    CapabilityImplementationIdentity Implementation,
    CapabilityIntegrityDigest ArtifactDigest,
    long ActivationRevision,
    string TemplateId,
    long TemplateVersion,
    IReadOnlyList<CommandActionSlotDefinition> Slots,
    IReadOnlyList<CommandActionArgumentPart> Arguments,
    IReadOnlyList<CommandActionEnvironmentEntry> Environment,
    CommandActionSecondaryGrammarPolicy SecondaryGrammar,
    CommandActionStandardInputKind StandardInput,
    string? StandardInputSlot,
    CommandActionOutputKind Output,
    CommandActionIsolationPolicy Isolation,
    bool RequiresCredentialChannel,
    string ContentHash)
{
    /// <summary>Gets a defensive immutable copy of the slot declarations.</summary>
    public IReadOnlyList<CommandActionSlotDefinition> Slots { get; } = Slots is null ? null! : Array.AsReadOnly(Slots.ToArray());
    /// <summary>Gets a defensive immutable copy of the argument declarations.</summary>
    public IReadOnlyList<CommandActionArgumentPart> Arguments { get; } = Arguments is null ? null! : Array.AsReadOnly(Arguments.ToArray());
    /// <summary>Gets a defensive immutable copy of the environment declarations.</summary>
    public IReadOnlyList<CommandActionEnvironmentEntry> Environment { get; } = Environment is null ? null! : Array.AsReadOnly(Environment.ToArray());
}
