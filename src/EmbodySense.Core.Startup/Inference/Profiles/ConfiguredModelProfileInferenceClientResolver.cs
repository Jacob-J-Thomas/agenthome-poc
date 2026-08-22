using System.Globalization;
using EmbodySense.Core.Application.Inference;
using EmbodySense.Core.Application.Inference.Profiles;
using EmbodySense.Core.Application.Inference.Profiles.Models;
using EmbodySense.Core.Common.Inference.Models;
using EmbodySense.Core.Common.Inference.Profiles;
using EmbodySense.Core.Common.Inference.Profiles.Models;
using EmbodySense.Core.Common.Loops.Custom.Execution;

namespace EmbodySense.Core.Startup.Inference.Profiles;

/// <summary>Resolves one fresh Codex client only from the exact configured profile and admitted hard-bound posture.</summary>
public sealed class ConfiguredModelProfileInferenceClientResolver : IExactModelProfileInferenceClientResolver
{
    private readonly LlmInferenceClientOptions _options;
    private readonly ConfiguredModelProfileRegistry _registry;
    private readonly IModelProfileAdapterRegistry _admissionAdapterRegistry;

    /// <summary>Creates an exact configured-profile resolver without exposing private host options.</summary>
    public ConfiguredModelProfileInferenceClientResolver(
        LlmInferenceClientOptions options,
        ConfiguredModelProfileRegistry registry,
        IModelProfileAdapterRegistry? admissionAdapterRegistry = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        _registry = registry ?? throw new ArgumentNullException(nameof(registry));
        _admissionAdapterRegistry = admissionAdapterRegistry ?? registry;
        if (options.Surface != LlmInferenceSurface.OpenAiCodex
            || string.IsNullOrWhiteSpace(options.WorkingDirectory)
            || string.IsNullOrWhiteSpace(options.Model))
        {
            throw new ArgumentException("The configured model resolver requires one local Codex workspace.", nameof(options));
        }

        _options = options with { WorkingDirectory = Path.GetFullPath(options.WorkingDirectory) };
    }

    /// <inheritdoc />
    public async Task<ExactModelProfileInferenceClientResolution> ResolveAsync(
        ExactModelProfileInferenceClientRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            if (!GovernedModelContractValidator.IsValid(request.Primary)
                || !GovernedModelContractValidator.IsValid(request.AttemptIdentity)
                || !GovernedModelContractValidator.IsValid(request.Reservation)
                || !GovernedModelContractValidator.IsValid(request.BudgetPolicy))
            {
                return Result(ExactModelProfileInferenceClientResolutionStatus.Ineligible);
            }

            var metadata = await _registry.ReadAsync(request.Primary.Capability.DescriptorIdentity.Id, cancellationToken).ConfigureAwait(false);
            var adapter = await _admissionAdapterRegistry.ReadPostureAsync(request.Primary.Metadata, cancellationToken).ConfigureAwait(false);
            if (metadata.Status != ModelProfileSourceReadStatus.Found
                || metadata.Metadata is null
                || !string.Equals(metadata.Metadata.ContentHash, request.Primary.Metadata.ContentHash, StringComparison.Ordinal)
                || !string.Equals(metadata.SourceRevisionHash, request.Primary.ProfileSourceRevisionHash, StringComparison.Ordinal)
                || adapter.Status != ModelProfileAdapterPostureStatus.Ready
                || !string.Equals(adapter.RegistryRevisionHash, request.Primary.AdapterRegistryRevisionHash, StringComparison.Ordinal)
                || !string.Equals(request.Primary.Metadata.ProviderId, "openai", StringComparison.Ordinal)
                || !string.Equals(request.Primary.Metadata.AdapterId, "codex-app-server", StringComparison.Ordinal)
                || !string.Equals(request.Primary.Metadata.ModelId, _options.Model, StringComparison.Ordinal))
            {
                return Result(ExactModelProfileInferenceClientResolutionStatus.Ineligible);
            }

            if (!EveryHardBoundIsEnforceable(request.Reservation, request.Primary.Metadata.UsageSupport))
            {
                return Result(ExactModelProfileInferenceClientResolutionStatus.Ineligible);
            }

            var enforcementHash = CustomLoopTraceContentHash.Compute(string.Join('\n',
                "embodysense.configured-model-profile-enforcement.v1",
                request.Primary.ContentHash,
                request.AttemptIdentity.ContentHash,
                request.Reservation.ContentHash,
                request.BudgetPolicy.ContentHash,
                request.RoutingAdmissionHash,
                request.AdmissionReceiptHash,
                request.AuthorityEvidenceHash,
                request.DataPostureEvidenceHash,
                request.ProviderAttemptId,
                request.ProviderCorrelationId,
                ((int)_options.Surface).ToString(CultureInfo.InvariantCulture)));
            var acknowledgement = new ExactModelProfileEnforcementAcknowledgement(
                request.Primary.ContentHash,
                request.AttemptIdentity.ContentHash,
                request.Reservation.ContentHash,
                request.BudgetPolicy.ContentHash,
                request.RoutingAdmissionHash,
                request.AdmissionReceiptHash,
                request.AuthorityEvidenceHash,
                request.DataPostureEvidenceHash,
                request.Primary.Metadata.ProviderId,
                _options.Surface,
                request.ProviderAttemptId,
                request.ProviderCorrelationId,
                enforcementHash);
            var executable = await _registry.AcquireExecutableSnapshotAsync(cancellationToken).ConfigureAwait(false);
            LlmInferenceClient? client = null;
            try
            {
                client = new LlmInferenceClient(
                    _options with
                    {
                        Model = request.Primary.Metadata.ModelId,
                        CodexExecutablePath = executable.ExecutablePath,
                    },
                    request.ToolBroker);
                await client.PrepareProviderAsync(cancellationToken).ConfigureAwait(false);
                return new ExactModelProfileInferenceClientResolution(
                    ExactModelProfileInferenceClientResolutionStatus.Resolved,
                    new ConfiguredModelProfileInferenceClientLease(request.Primary, acknowledgement, client, executable));
            }
            catch
            {
                if (client is not null)
                {
                    await client.DisposeAsync().ConfigureAwait(false);
                }
                await executable.DisposeAsync().ConfigureAwait(false);
                throw;
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            return Result(ExactModelProfileInferenceClientResolutionStatus.Unavailable);
        }
    }

    private static bool EveryHardBoundIsEnforceable(
        GovernedModelUsageCeiling reservation,
        GovernedModelUsageSupportPolicy support)
        => Enforceable(reservation.InputTokens.IsBounded, support.InputTokens)
            && Enforceable(reservation.OutputTokens.IsBounded, support.OutputTokens)
            && Enforceable(reservation.CachedTokens.IsBounded, support.CachedTokens)
            && Enforceable(reservation.TotalTokens.IsBounded, support.TotalTokens)
            && Enforceable(reservation.MonetaryCost.IsBounded, support.MonetaryCost);

    private static bool Enforceable(bool bounded, GovernedModelUsageSupport support)
        => !bounded || support == GovernedModelUsageSupport.AuthoritativeAndHardBoundedAtDispatch;

    private static ExactModelProfileInferenceClientResolution Result(
        ExactModelProfileInferenceClientResolutionStatus status)
        => new(status, null);

}
