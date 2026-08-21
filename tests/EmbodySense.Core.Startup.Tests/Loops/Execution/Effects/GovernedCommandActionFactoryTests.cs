using EmbodySense.Core.Application.CommandActions;
using EmbodySense.Core.Application.Capabilities.Models;
using EmbodySense.Core.Application.CommandActions.Models;
using EmbodySense.Core.Clients.Capabilities;
using EmbodySense.Core.Clients.CommandActions;
using EmbodySense.Core.Common.Capabilities;
using EmbodySense.Core.Common.Capabilities.Models;
using EmbodySense.Core.Common.CommandActions;
using EmbodySense.Core.Common.CommandActions.Models;
using EmbodySense.Core.Common.Workspace;
using EmbodySense.Core.Startup.Loops.Execution.Effects;
using EmbodySense.Tests.Support;

namespace EmbodySense.Core.Startup.Tests.Loops.Execution.Effects;

public sealed class GovernedCommandActionFactoryTests
{
    [Fact]
    public void Factory_composes_one_finite_exact_canonical_operation_registry()
    {
        using var workspace = new TestWorkspace();
        var registration = Registration();

        var registry = GovernedCommandActionFactory.CreateRegistry(
            new WorkspacePaths(workspace.RootPath),
            [registration],
            DenyingCapabilityExecutableArtifactResolver.Instance,
            DenyingCommandActionProcessIsolationBoundary.Instance);

        var descriptor = Assert.Single(registry.Descriptors);
        Assert.Equal(GovernedCommandActionOperation.CreateOperationId(registration.Template), descriptor.OperationId);
        Assert.True(registry.TryResolve(descriptor, out var operation));
        Assert.NotNull(operation);
    }

    [Fact]
    public void Factory_composes_distinct_operations_for_valid_revisions_of_one_template_identity()
    {
        using var workspace = new TestWorkspace();
        var registration = Registration();
        var revised = WithTemplateVersion(registration, 2);

        var registry = GovernedCommandActionFactory.CreateRegistry(
            new WorkspacePaths(workspace.RootPath),
            [registration, revised],
            DenyingCapabilityExecutableArtifactResolver.Instance,
            DenyingCommandActionProcessIsolationBoundary.Instance);

        Assert.Equal(2, registry.Descriptors.Count);
        Assert.NotEqual(registration.Template.ContentHash, revised.Template.ContentHash);
        Assert.Contains(registry.Descriptors, descriptor => descriptor.OperationId == GovernedCommandActionOperation.CreateOperationId(registration.Template));
        Assert.Contains(registry.Descriptors, descriptor => descriptor.OperationId == GovernedCommandActionOperation.CreateOperationId(revised.Template));
    }

    [Fact]
    public void Factory_rejects_duplicate_or_incoherent_registration_before_composition()
    {
        using var workspace = new TestWorkspace();
        var paths = new WorkspacePaths(workspace.RootPath);
        var registration = Registration();

        Assert.Throws<ArgumentException>(() => GovernedCommandActionFactory.CreateRegistry(
            paths, [registration, registration], DenyingCapabilityExecutableArtifactResolver.Instance, DenyingCommandActionProcessIsolationBoundary.Instance));
        Assert.Throws<ArgumentException>(() => GovernedCommandActionFactory.CreateRegistry(
            paths, [registration with { Template = registration.Template with { TemplateVersion = 2 } }], DenyingCapabilityExecutableArtifactResolver.Instance, DenyingCommandActionProcessIsolationBoundary.Instance));
        Assert.Throws<ArgumentNullException>(() => GovernedCommandActionFactory.CreateRegistry(
            paths, [registration], null!, DenyingCommandActionProcessIsolationBoundary.Instance));
    }

    [Fact]
    public void Runtime_provider_reports_only_registered_credential_free_isolation_as_available()
    {
        var registration = Registration();
        var deniedProvider = new CommandActionRuntimeProvider(
            [registration],
            DenyingCapabilityExecutableArtifactResolver.Instance,
            DenyingCommandActionProcessIsolationBoundary.Instance);
        var availableProvider = new CommandActionRuntimeProvider(
            [registration],
            DenyingCapabilityExecutableArtifactResolver.Instance,
            AvailableCommandActionProcessIsolationBoundary.Instance);
        Assert.False(deniedProvider.IsIsolationAvailable(registration));
        Assert.True(availableProvider.IsIsolationAvailable(registration));
        Assert.False(new CommandActionRuntimeProvider(
            [registration],
            DenyingCapabilityExecutableArtifactResolver.Instance,
            ThrowingCommandActionProcessIsolationBoundary.Instance).IsIsolationAvailable(registration));
        Assert.False(availableProvider.IsIsolationAvailable(Registration() with
        {
            Template = registration.Template with { TemplateVersion = 2 },
        }));
        var workspaceTarget = Registration(workspaceTarget: true);
        Assert.False(new CommandActionRuntimeProvider(
            [workspaceTarget],
            DenyingCapabilityExecutableArtifactResolver.Instance,
            AvailableCommandActionProcessIsolationBoundary.Instance).IsIsolationAvailable(workspaceTarget));
    }

    [Fact]
    public void Credential_bearing_command_remains_unavailable_even_with_isolation()
    {
        var registration = Registration(requiresCredentialChannel: true);
        var provider = new CommandActionRuntimeProvider(
            [registration],
            DenyingCapabilityExecutableArtifactResolver.Instance,
            AvailableCommandActionProcessIsolationBoundary.Instance);
        Assert.False(provider.IsIsolationAvailable(registration));
    }

    internal static CommandActionRegistration Registration(bool requiresCredentialChannel = false, bool workspaceTarget = false)
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
            "Execute one governed command.", schema!, schema!, resources,
            CapabilitySideEffectClass.LocalReversible,
            new CapabilityAccessRequirements([], CapabilityEgressMode.None, [], requiresCredentialChannel ? [secret!] : []));
        var manifest = new CapabilityArtifactManifest(
            1, descriptor,
            new CapabilityArtifactSourceReference(CapabilityArtifactSourceKind.Local, uri, "rev-1", CapabilityArtifactUpdatePolicy.Pinned),
            digest, null, platform!, "command.exe", []);
        Assert.True(CapabilityDescriptorIdentity.TryCreate(descriptor, out var identity, out _));
        var slots = workspaceTarget
            ? new[] { new CommandActionSlotDefinition("target", CommandActionSlotKind.WorkspaceRelativeTarget, 512, null, null, [], false) }
            : [];
        var arguments = workspaceTarget
            ? new[] { new CommandActionArgumentPart(CommandActionArgumentPartKind.Slot, "target") }
            : [];
        var template = CommandActionTemplateContract.Create(
            1, identity!, implementation, digest, 3, "command/render", 1,
            slots, arguments, [], CommandActionSecondaryGrammarPolicy.None, CommandActionStandardInputKind.Closed, null, CommandActionOutputKind.Json,
            new CommandActionIsolationPolicy(CommandActionWorkingDirectoryKind.ArtifactRoot, CommandActionNetworkPolicy.Denied, 5_000, 2_000, 64_000_000, 16_384, 1, true),
            requiresCredentialChannel);
        return new CommandActionRegistration(template, manifest);
    }

    internal static CommandActionRegistration TypedRegistration()
    {
        var registration = Registration();
        var template = CommandActionTemplateContract.Create(
            registration.Template.SchemaVersion,
            registration.Template.Capability,
            registration.Template.Implementation,
            registration.Template.ArtifactDigest,
            registration.Template.ActivationRevision,
            registration.Template.TemplateId,
            registration.Template.TemplateVersion,
            [
                new CommandActionSlotDefinition("identifier", CommandActionSlotKind.Identifier, 128, null, null, [], false),
                new CommandActionSlotDefinition("input", CommandActionSlotKind.BoundedJson, 512, null, null, [], false),
                new CommandActionSlotDefinition("literal", CommandActionSlotKind.BoundedText, 8, null, null, [], false),
            ],
            [
                new CommandActionArgumentPart(CommandActionArgumentPartKind.Slot, "literal"),
                new CommandActionArgumentPart(CommandActionArgumentPartKind.Slot, "identifier"),
            ],
            [],
            CommandActionSecondaryGrammarPolicy.None,
            CommandActionStandardInputKind.SlotJson,
            "input",
            CommandActionOutputKind.Json,
            registration.Template.Isolation,
            false);
        return registration with { Template = template };
    }

    private static CommandActionRegistration WithTemplateVersion(CommandActionRegistration registration, long templateVersion)
    {
        var template = registration.Template;
        return registration with
        {
            Template = CommandActionTemplateContract.Create(
                template.SchemaVersion,
                template.Capability,
                template.Implementation,
                template.ArtifactDigest,
                template.ActivationRevision,
                template.TemplateId,
                templateVersion,
                template.Slots,
                template.Arguments,
                template.Environment,
                template.SecondaryGrammar,
                template.StandardInput,
                template.StandardInputSlot,
                template.Output,
                template.Isolation,
                template.RequiresCredentialChannel),
        };
    }

}
