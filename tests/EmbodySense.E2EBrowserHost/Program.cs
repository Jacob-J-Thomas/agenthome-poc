using System.Text.Json;
using EmbodySense.Core.Application.Capabilities.Models;
using EmbodySense.Core.Application.CommandActions.Models;
using EmbodySense.Core.Application.Inference.Profiles;
using EmbodySense.Core.Application.Inference.Profiles.Models;
using EmbodySense.Core.Common.Capabilities;
using EmbodySense.Core.Common.Capabilities.Models;
using EmbodySense.Core.Common.CommandActions;
using EmbodySense.Core.Common.CommandActions.Models;
using EmbodySense.Core.Common.Inference.Profiles.Models;
using EmbodySense.Core.Common.Loops.Custom.Execution;
using EmbodySense.Core.Startup.Capabilities;
using EmbodySense.Core.Startup.HumanReview;
using EmbodySense.Core.Startup.HumanInput;
using EmbodySense.Core.Startup.Inference.Profiles;
using EmbodySense.Core.Startup.Loops.Execution;
using EmbodySense.Core.Startup.Loops.Execution.Effects;
using EmbodySense.Core.Startup.Runtime;
using EmbodySense.Core.Startup.Workspace;
using EmbodySense.Core.Common.Workspace;
using EmbodySense.Core.Persistence.Capabilities;
using EmbodySense.E2EBrowserHost;
using EmbodySense.Web;
using EmbodySense.Web.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

return await BrowserProfileWebHost.RunAsync(args);

namespace EmbodySense.E2EBrowserHost
{
    public sealed record BrowserModelProfileSpec(
        string Id,
        string ImplementationSuffix,
        string Purpose,
        string ModelId,
        bool Ready);

    public static class BrowserProfileWebHost
    {
        private static readonly JsonSerializerOptions _jsonOptions = new(JsonSerializerDefaults.Web);

        public static async Task<int> RunAsync(string[] args)
        {
            var capabilityTrustRoot = RequiredOption(args, "--capability-trust-root");
            var specs = OptionValues(args, "--additional-model-profile")
                .Select(value => JsonSerializer.Deserialize<BrowserModelProfileSpec>(value, _jsonOptions)
                    ?? throw new ArgumentException("An additional browser model-profile specification was empty."))
                .ToArray();
            var commandActions = OptionValues(args, "--command-action-registration")
                .Select(value => JsonSerializer.Deserialize<BrowserCommandActionSpec>(value, _jsonOptions)
                    ?? throw new ArgumentException("A browser command Action specification was empty."))
                .Select(CreateCommandActionRegistration)
                .ToArray();
            var providers = specs.Select(CreateRuntimeProvider).ToArray();
            var options = WebRunOptions.FromArguments(args);
            var commandActionProvider = commandActions.Length == 0
                ? null
                : new CommandActionRuntimeProvider(
                    commandActions,
                    new CapabilityArtifactStore(
                        new WorkspacePaths(options.WorkingDirectory),
                        new FileCapabilityArtifactStateTrustProvider(capabilityTrustRoot),
                        BrowserCommandActionArtifactTrustVerifier.Instance),
                    BrowserCommandActionProcessIsolationBoundary.Instance);
            var builder = EmbodySense.Web.Program.CreateBuilder(args, options);
            if (args.Contains("--suppress-governed-background-host-for-test", StringComparer.Ordinal))
            {
                var backgroundHostRegistrations = builder.Services
                    .Where(service => service.ServiceType == typeof(Microsoft.Extensions.Hosting.IHostedService) && service.ImplementationFactory is not null)
                    .ToArray();
                if (backgroundHostRegistrations.Length != 1)
                {
                    throw new InvalidOperationException("The test host could not identify exactly one application-owned governed background registration.");
                }

                builder.Services.Remove(backgroundHostRegistrations[0]);
            }

            builder.Services.RemoveAll<WebAgentRuntimeHost>();
            builder.Services.AddSingleton(provider =>
            {
                var approval = provider.GetRequiredService<WebApprovalCoordinator>();
                var publication = provider.GetRequiredService<IAgentRuntimeConversationPublicationObserver>();
                var decisionAuthorizationProvider = provider.GetRequiredService<IHumanReviewDecisionAuthorizationProvider>();
                var humanInputAuthorityProvider = provider.GetRequiredService<IAgentRuntimeHumanInputAuthorityProvider>();
                var humanInputCandidateRegistry = provider.GetRequiredService<IHumanInputSupersedeCandidateRegistry>();
                return new WebAgentRuntimeHost(
                    options,
                    approval,
                    WorkspaceInitializer.ForFileCapabilityTrustRoot(capabilityTrustRoot),
                    publication,
                    status => AgentRuntimeFactory.ForFileCapabilityTrustRoot(
                        approval,
                        capabilityTrustRoot,
                        status,
                        publication,
                        additionalModelProfileProviders: providers,
                        commandActionRuntimeProvider: commandActionProvider)
                        .WithHumanReviewDecisionAuthorizationProvider(decisionAuthorizationProvider)
                        .WithHumanInputAuthorityProvider(humanInputAuthorityProvider)
                        .WithHumanInputSupersedeCandidateRegistry(humanInputCandidateRegistry));
            });
            await using var application = builder.Build();
            EmbodySense.Web.Program.ConfigurePipeline(application);
            await application.RunAsync();
            return 0;
        }

        public static CapabilityDescriptor CreateDescriptor(BrowserModelProfileSpec spec)
        {
            ArgumentNullException.ThrowIfNull(spec);
            var template = BuiltInCapabilityCatalog.Descriptors.Single(descriptor =>
                string.Equals(descriptor.Id.Value, BuiltInCapabilityCatalog.CodexModelProfileCapabilityId, StringComparison.Ordinal));
            if (!CapabilityId.TryParse(spec.Id, out var id, out _)
                || !CapabilityProviderId.TryParse("org.example", out var provider, out _))
            {
                throw new ArgumentException("The browser model-profile identity is invalid.", nameof(spec));
            }

            return template with
            {
                Id = id!,
                Implementation = new CapabilityImplementationIdentity(provider!, $"model-profile/{spec.ImplementationSuffix}"),
                Provenance = new CapabilityProvenance(
                    CapabilityProvenanceKind.BuiltIn,
                    $"https://example.invalid/browser-e2e/model-profile/{spec.ImplementationSuffix}",
                    "browser-e2e-v1",
                    null),
                Purpose = spec.Purpose,
            };
        }

        public static string Serialize(BrowserModelProfileSpec spec)
            => JsonSerializer.Serialize(spec, _jsonOptions);

        public static string Serialize(BrowserCommandActionSpec spec)
            => JsonSerializer.Serialize(spec, _jsonOptions);

        public static CommandActionRegistration CreateCommandActionRegistration(BrowserCommandActionSpec spec)
        {
            ArgumentNullException.ThrowIfNull(spec);
            if (!CapabilityIntegrityDigest.TryParse(spec.ArtifactDigest, out var digest, out _)
                || string.IsNullOrWhiteSpace(spec.EntryPoint)
                || Path.GetFileName(spec.EntryPoint) != spec.EntryPoint)
            {
                throw new ArgumentException("The browser command Action specification is invalid.", nameof(spec));
            }
            if (!CapabilityId.TryParse("org.example/command/browser-json-echo", out var capabilityId, out _)
                || !CapabilityProviderId.TryParse("org.example", out var providerId, out _)
                || !CapabilityVersion.TryParse("1.0.0", out var version, out _)
                || !CapabilityVersionRange.TryParse("*", out var versionRange, out _)
                || !CapabilityJsonSchema.TryCreate($"{{\"$schema\":\"{CapabilityJsonSchema.Draft202012Dialect}\",\"type\":\"object\"}}", out var schema, out _))
            {
                throw new InvalidOperationException("The fixed browser command Action identity is invalid.");
            }
            const string SourceUri = "file:///browser-e2e/command-action";
            var implementation = new CapabilityImplementationIdentity(providerId!, "command/browser-json-echo");
            var descriptor = new CapabilityDescriptor(
                1,
                capabilityId!,
                CapabilityKind.Actuator,
                version!,
                implementation,
                new CapabilityProvenance(CapabilityProvenanceKind.LocalSource, SourceUri, "rev-1", digest),
                new CapabilityCompatibility(versionRange!, [CapabilityHostRuntime.Platform]),
                "Process one bounded JSON value through the controlled installed-browser command boundary.",
                schema!,
                schema!,
                new CapabilityResourceLimits(5_000, 128_000_000, 16_384, 1),
                CapabilitySideEffectClass.LocalReversible,
                new CapabilityAccessRequirements([], CapabilityEgressMode.None, [], []));
            var manifest = new CapabilityArtifactManifest(
                1,
                descriptor,
                new CapabilityArtifactSourceReference(CapabilityArtifactSourceKind.Local, SourceUri, "rev-1", CapabilityArtifactUpdatePolicy.Pinned),
                digest!,
                null,
                CapabilityHostRuntime.Platform,
                spec.EntryPoint,
                []);
            if (!CapabilityDescriptorIdentity.TryCreate(descriptor, out var identity, out var validation) || !validation.IsValid)
            {
                throw new InvalidOperationException("The fixed browser command Action descriptor is invalid.");
            }
            var arguments = OperatingSystem.IsWindows()
                ? new[]
                {
                    new CommandActionArgumentPart(CommandActionArgumentPartKind.Fixed, "/r"),
                    new CommandActionArgumentPart(CommandActionArgumentPartKind.Fixed, ".*"),
                }
                : [];
            var template = CommandActionTemplateContract.Create(
                1,
                identity!,
                implementation,
                digest!,
                1,
                "command/browser-json-echo",
                1,
                [new CommandActionSlotDefinition("input", CommandActionSlotKind.BoundedJson, 512, null, null, [], false)],
                arguments,
                [],
                CommandActionSecondaryGrammarPolicy.None,
                CommandActionStandardInputKind.SlotJson,
                "input",
                CommandActionOutputKind.Json,
                new CommandActionIsolationPolicy(CommandActionWorkingDirectoryKind.ArtifactRoot, CommandActionNetworkPolicy.Denied, 5_000, 2_000, 128_000_000, 16_384, 1, true),
                false);
            return new CommandActionRegistration(template, manifest);
        }

        private static ModelProfileRuntimeProvider CreateRuntimeProvider(BrowserModelProfileSpec spec)
        {
            var descriptor = CreateDescriptor(spec);
            if (!CapabilityDescriptorIdentity.TryCreate(descriptor, out var identity, out var validation))
            {
                throw new ArgumentException(string.Join(';', validation.Errors.Select(error => error.Message)), nameof(spec));
            }
            if (!CapabilityDataClass.TryParse("sensitive", out var sensitive, out _))
            {
                throw new InvalidOperationException("The canonical sensitive data class is unavailable.");
            }

            var metadata = GovernedModelProfileMetadata.Create(
                1,
                identity!,
                "openai",
                "browser-e2e-adapter",
                spec.ModelId,
                "v1",
                1,
                CustomLoopTraceContentHash.Compute($"browser-model-profile-configuration.v1\n{descriptor.Id.Value}\n{spec.ModelId}"),
                descriptor.Purpose,
                [GovernedModelModality.Text],
                [GovernedModelCapability.ToolCalling, GovernedModelCapability.Streaming],
                1,
                1,
                GovernedModelPrivacyPosture.Create(
                    1,
                    GovernedModelLocality.Remote,
                    CapabilityEgressMode.Unrestricted,
                    [],
                    [sensitive!],
                    [],
                    GovernedModelRetentionPosture.Indefinite,
                    GovernedModelTrainingPosture.Allowed),
                GovernedModelUsageSupportPolicy.Create(
                    GovernedModelUsageSupport.AuthoritativeAndHardBoundedAtDispatch,
                    GovernedModelUsageSupport.AuthoritativeAndHardBoundedAtDispatch,
                    GovernedModelUsageSupport.AuthoritativeAndHardBoundedAtDispatch,
                    GovernedModelUsageSupport.AuthoritativeAndHardBoundedAtDispatch,
                    GovernedModelUsageSupport.Unavailable),
                [],
                ["provider-inference"]);
            var sourceRevisionHash = CustomLoopTraceContentHash.Compute($"browser-model-profile-source.v1\n{metadata.ContentHash}");
            return new ModelProfileRuntimeProvider(
                new MetadataSource(
                    descriptor.Id,
                    metadata,
                    sourceRevisionHash),
                new AdapterRegistry(
                    metadata.ContentHash,
                    CustomLoopTraceContentHash.Compute($"browser-model-profile-registry.v1\n{metadata.ContentHash}"),
                    spec.Ready),
                admissionAdapterRegistry => new BrowserExactModelProfileResolver(metadata, sourceRevisionHash, admissionAdapterRegistry));
        }

        private static string RequiredOption(string[] args, string name)
            => OptionValues(args, name).SingleOrDefault()
                ?? throw new ArgumentException($"Option {name} is required exactly once.");

        private static IEnumerable<string> OptionValues(string[] args, string name)
        {
            for (var index = 0; index < args.Length; index++)
            {
                if (!string.Equals(args[index], name, StringComparison.Ordinal))
                {
                    continue;
                }
                if (index + 1 >= args.Length || args[index + 1].StartsWith('-'))
                {
                    throw new ArgumentException($"Option {name} requires a value.");
                }
                yield return args[++index];
            }
        }

        private sealed class MetadataSource(
            CapabilityId profileId,
            GovernedModelProfileMetadata metadata,
            string sourceRevisionHash) : IModelProfileMetadataSource
        {
            public Task<ModelProfileSourceReadResult> ReadAsync(CapabilityId requestedProfileId, CancellationToken cancellationToken = default)
            {
                cancellationToken.ThrowIfCancellationRequested();
                return Task.FromResult(requestedProfileId.Equals(profileId)
                    ? new ModelProfileSourceReadResult(ModelProfileSourceReadStatus.Found, metadata, sourceRevisionHash)
                    : new ModelProfileSourceReadResult(ModelProfileSourceReadStatus.NotFound, null, null));
            }
        }

        private sealed class AdapterRegistry(
            string metadataHash,
            string registryRevisionHash,
            bool ready) : IModelProfileAdapterRegistry
        {
            public Task<ModelProfileAdapterPosture> ReadPostureAsync(GovernedModelProfileMetadata metadata, CancellationToken cancellationToken = default)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var ownsProfile = string.Equals(metadata.ContentHash, metadataHash, StringComparison.Ordinal);
                return Task.FromResult(new ModelProfileAdapterPosture(
                    ownsProfile
                        ? ready ? ModelProfileAdapterPostureStatus.Ready : ModelProfileAdapterPostureStatus.Unavailable
                        : ModelProfileAdapterPostureStatus.Unregistered,
                    metadata.ContentHash,
                    registryRevisionHash));
            }
        }

    }
}
