using EmbodySense.Core.Application.Capabilities.Models;
using EmbodySense.Core.Application.CommandActions.Models;
using EmbodySense.Core.Common.Capabilities;
using EmbodySense.Core.Common.Capabilities.Models;
using EmbodySense.Core.Common.CommandActions;
using EmbodySense.Core.Common.CommandActions.Models;
using EmbodySense.Core.Common.Loops.Execution.Effects;
using EmbodySense.Core.Common.Loops.Execution.Effects.Models;

namespace EmbodySense.Core.Application.Tests.CommandActions;

internal static class CommandActionApplicationTestData
{
    internal static CommandActionRegistration Registration(bool credentials = false)
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
        var resources = new CapabilityResourceLimits(5_000, 64_000_000, 16_384, 1);
        var uri = "file:///sources/command.exe";
        var descriptor = new CapabilityDescriptor(
            1, id!, CapabilityKind.Actuator, version!, implementation,
            new CapabilityProvenance(CapabilityProvenanceKind.LocalSource, uri, "rev-1", digest),
            new CapabilityCompatibility(range!, [platform!]),
            "Execute one governed test command.", schema!, schema!, resources,
            CapabilitySideEffectClass.LocalReversible,
            new CapabilityAccessRequirements([], CapabilityEgressMode.None, [], credentials ? [secret!] : []));
        var manifest = new CapabilityArtifactManifest(
            1, descriptor,
            new CapabilityArtifactSourceReference(CapabilityArtifactSourceKind.Local, uri, "rev-1", CapabilityArtifactUpdatePolicy.Pinned),
            digest, null, platform!, "command.exe", []);
        Assert.True(CapabilityDescriptorIdentity.TryCreate(descriptor, out var identity, out _));
        var template = CommandActionTemplateContract.Create(
            1, identity!, implementation, digest, 3, "command/render", 1,
            [new CommandActionSlotDefinition("value", CommandActionSlotKind.BoundedText, 256, null, null, [], false)],
            [new CommandActionArgumentPart(CommandActionArgumentPartKind.Slot, "value")],
            [], CommandActionSecondaryGrammarPolicy.None, CommandActionStandardInputKind.Closed, null, CommandActionOutputKind.Json,
            new CommandActionIsolationPolicy(CommandActionWorkingDirectoryKind.ArtifactRoot, CommandActionNetworkPolicy.Denied, 5_000, 2_000, 64_000_000, 16_384, 1, true),
            credentials);
        return new CommandActionRegistration(template, manifest);
    }

    internal static GovernedActuatorInputEvidence Input(CommandActionRegistration registration, string value = "literal ; $(data)")
    {
        var input = new CommandActionInput(
            1, registration.Template.TemplateId, registration.Template.TemplateVersion, registration.Template.ContentHash,
            [new CommandActionSlotValue("value", CommandActionSlotKind.BoundedText, value)]);
        Assert.True(GovernedActuatorInputContract.TryCanonicalize(CommandActionInputContract.Encode(input, registration.Template), out var canonical, out _));
        return canonical!;
    }
}
