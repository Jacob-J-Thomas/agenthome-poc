using System.Text.Json;
using EmbodySense.Core.Application.Inference.Profiles;
using EmbodySense.Core.Application.Inference.Profiles.Models;
using EmbodySense.Core.Common.Capabilities;
using EmbodySense.Core.Common.Capabilities.Models;
using EmbodySense.Core.Common.Inference.Profiles.Models;
using EmbodySense.Core.Common.Loops.Custom.Execution;
using EmbodySense.Core.Startup.Capabilities;
using EmbodySense.Core.Startup.Inference.Profiles;
using EmbodySense.Core.Startup.Loops.Execution;
using EmbodySense.Core.Startup.Runtime;
using EmbodySense.Core.Startup.Workspace;
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
            var providers = specs.Select(CreateRuntimeProvider).ToArray();
            var options = WebRunOptions.FromArguments(args);
            var builder = EmbodySense.Web.Program.CreateBuilder(args, options);
            builder.Services.RemoveAll<WebAgentRuntimeHost>();
            builder.Services.AddSingleton(provider =>
            {
                var approval = provider.GetRequiredService<WebApprovalCoordinator>();
                var publication = provider.GetRequiredService<IAgentRuntimeConversationPublicationObserver>();
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
                        additionalModelProfileProviders: providers));
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
                    GovernedModelUsageSupport.AuthoritativeAfterDispatch,
                    GovernedModelUsageSupport.AuthoritativeAfterDispatch,
                    GovernedModelUsageSupport.AuthoritativeAfterDispatch,
                    GovernedModelUsageSupport.AuthoritativeAfterDispatch,
                    GovernedModelUsageSupport.Unavailable),
                [],
                ["provider-inference"]);
            return new ModelProfileRuntimeProvider(
                new MetadataSource(
                    descriptor.Id,
                    metadata,
                    CustomLoopTraceContentHash.Compute($"browser-model-profile-source.v1\n{metadata.ContentHash}")),
                new AdapterRegistry(
                    metadata.ContentHash,
                    CustomLoopTraceContentHash.Compute($"browser-model-profile-registry.v1\n{metadata.ContentHash}"),
                    spec.Ready),
                _ => IneligibleResolver.Instance);
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

        private sealed class IneligibleResolver : IExactModelProfileInferenceClientResolver
        {
            public static IneligibleResolver Instance { get; } = new();

            public Task<ExactModelProfileInferenceClientResolution> ResolveAsync(
                ExactModelProfileInferenceClientRequest request,
                CancellationToken cancellationToken = default)
            {
                ArgumentNullException.ThrowIfNull(request);
                cancellationToken.ThrowIfCancellationRequested();
                return Task.FromResult(new ExactModelProfileInferenceClientResolution(
                    ExactModelProfileInferenceClientResolutionStatus.Ineligible,
                    null));
            }
        }
    }
}
