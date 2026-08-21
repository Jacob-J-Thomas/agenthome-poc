using EmbodySense.Core.Application.Capabilities.Models;
using EmbodySense.Core.Application.CommandActions.Models;
using EmbodySense.Core.Common.Capabilities;
using EmbodySense.Core.Common.Capabilities.Models;
using EmbodySense.Core.Common.CommandActions;
using EmbodySense.Core.Common.CommandActions.Models;
using EmbodySense.Core.Common.Loops.Execution.Effects;
using EmbodySense.Core.Common.Loops.Execution.Effects.Models;

namespace EmbodySense.Core.Clients.Tests.CommandActions;

internal static class CommandActionClientTestData
{
    internal static CommandActionRegistration Registration(
        string entryPoint = "command.exe",
        int executionMilliseconds = 5_000,
        int terminationMilliseconds = 2_000,
        int outputBytes = 16_384,
        bool credentials = false)
    {
        Assert.True(CapabilityId.TryParse("org.example/command", out var id, out _));
        Assert.True(CapabilityProviderId.TryParse("org.example", out var provider, out _));
        Assert.True(CapabilityVersion.TryParse("1.0.0", out var version, out _));
        Assert.True(CapabilityVersionRange.TryParse("*", out var range, out _));
        Assert.True(CapabilityPlatform.TryParse("windows/x64", out var platform, out _));
        Assert.True(CapabilityJsonSchema.TryCreate($"{{\"$schema\":\"{CapabilityJsonSchema.Draft202012Dialect}\",\"type\":\"object\"}}", out var schema, out _));
        Assert.True(CapabilitySecretRequirement.TryParse("api_token", out var secret, out _));
        var digest = CapabilityIntegrityDigest.Compute("command-artifact"u8);
        var implementation = new CapabilityImplementationIdentity(provider!, "command/runner");
        var resources = new CapabilityResourceLimits(executionMilliseconds, 64_000_000, outputBytes, 1);
        var uri = "file:///sources/command.exe";
        var descriptor = new CapabilityDescriptor(
            1, id!, CapabilityKind.Actuator, version!, implementation,
            new CapabilityProvenance(CapabilityProvenanceKind.LocalSource, uri, "rev-1", digest),
            new CapabilityCompatibility(range!, [platform!]),
            "Execute one governed command harness.", schema!, schema!, resources,
            CapabilitySideEffectClass.LocalReversible,
            new CapabilityAccessRequirements([], CapabilityEgressMode.None, [], credentials ? [secret!] : []));
        var manifest = new CapabilityArtifactManifest(
            1, descriptor,
            new CapabilityArtifactSourceReference(CapabilityArtifactSourceKind.Local, uri, "rev-1", CapabilityArtifactUpdatePolicy.Pinned),
            digest, null, platform!, entryPoint, []);
        Assert.True(CapabilityDescriptorIdentity.TryCreate(descriptor, out var identity, out _));
        var template = CommandActionTemplateContract.Create(
            1, identity!, implementation, digest, 3, "command/harness", 1,
            [
                new CommandActionSlotDefinition("behavior", CommandActionSlotKind.Enumeration, 64, null, null, ["hang", "invalid-encoding", "literal", "malformed", "nonzero", "overflow", "unicode-boundary"], false),
                new CommandActionSlotDefinition("input", CommandActionSlotKind.BoundedText, 4_096, null, null, [], false),
                new CommandActionSlotDefinition("value", CommandActionSlotKind.BoundedText, 4_096, null, null, [], false),
            ],
            [
                new CommandActionArgumentPart(CommandActionArgumentPartKind.Fixed, "command-action"),
                new CommandActionArgumentPart(CommandActionArgumentPartKind.Slot, "behavior"),
                new CommandActionArgumentPart(CommandActionArgumentPartKind.Slot, "value"),
            ],
            [new CommandActionEnvironmentEntry("A", "literal"), new CommandActionEnvironmentEntry("Z", "governed")],
            CommandActionSecondaryGrammarPolicy.None,
            CommandActionStandardInputKind.SlotUtf8, "input", CommandActionOutputKind.Json,
            new CommandActionIsolationPolicy(CommandActionWorkingDirectoryKind.ArtifactRoot, CommandActionNetworkPolicy.Denied, executionMilliseconds, terminationMilliseconds, 64_000_000, outputBytes, 1, true),
            credentials);
        return new CommandActionRegistration(template, manifest);
    }

    internal static GovernedActuatorInputEvidence Input(CommandActionRegistration registration, string behavior, string value = "space ; && $(literal) Ω", string input = "stdin literal")
    {
        var semantic = new CommandActionInput(
            1, registration.Template.TemplateId, registration.Template.TemplateVersion, registration.Template.ContentHash,
            [
                new CommandActionSlotValue("behavior", CommandActionSlotKind.Enumeration, behavior),
                new CommandActionSlotValue("input", CommandActionSlotKind.BoundedText, input),
                new CommandActionSlotValue("value", CommandActionSlotKind.BoundedText, value),
            ]);
        Assert.True(GovernedActuatorInputContract.TryCanonicalize(CommandActionInputContract.Encode(semantic, registration.Template), out var canonical, out _));
        return canonical!;
    }
}
