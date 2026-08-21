using EmbodySense.Core.Application.Inference.Profiles;
using EmbodySense.Core.Application.Inference.Profiles.Models;
using EmbodySense.Core.Common.Inference;
using EmbodySense.Core.Common.Inference.Models;
using EmbodySense.Core.Common.Inference.Profiles.Models;
using EmbodySense.Core.Common.Loops.Custom.Execution;

namespace EmbodySense.E2EBrowserHost;

internal sealed class BrowserExactModelProfileResolver(
    GovernedModelProfileMetadata metadata,
    string sourceRevisionHash,
    IModelProfileAdapterRegistry admissionAdapterRegistry) : IExactModelProfileInferenceClientResolver
{
    public async Task<ExactModelProfileInferenceClientResolution> ResolveAsync(
        ExactModelProfileInferenceClientRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();
        if (!string.Equals(request.Primary.Metadata.ContentHash, metadata.ContentHash, StringComparison.Ordinal)
            || !string.Equals(request.Primary.ProfileSourceRevisionHash, sourceRevisionHash, StringComparison.Ordinal)
            || request.Reservation.OutputTokens is { IsBounded: true } output
                && output.Maximum != metadata.MaximumOutputTokens)
        {
            return new ExactModelProfileInferenceClientResolution(ExactModelProfileInferenceClientResolutionStatus.Ineligible, null);
        }

        var posture = await admissionAdapterRegistry.ReadPostureAsync(metadata, cancellationToken).ConfigureAwait(false);
        if (posture.Status != ModelProfileAdapterPostureStatus.Ready
            || !string.Equals(request.Primary.AdapterRegistryRevisionHash, posture.RegistryRevisionHash, StringComparison.Ordinal))
        {
            return new ExactModelProfileInferenceClientResolution(ExactModelProfileInferenceClientResolutionStatus.Ineligible, null);
        }

        var acknowledgement = new ExactModelProfileEnforcementAcknowledgement(
            request.Primary.ContentHash,
            request.AttemptIdentity.ContentHash,
            request.Reservation.ContentHash,
            request.BudgetPolicy.ContentHash,
            request.RoutingAdmissionHash,
            request.AdmissionReceiptHash,
            request.AuthorityEvidenceHash,
            request.DataPostureEvidenceHash,
            metadata.ProviderId,
            LlmInferenceSurface.OpenAiCodex,
            request.ProviderAttemptId,
            request.ProviderCorrelationId,
            CustomLoopTraceContentHash.Compute("browser-e2e-exact-model-profile-enforcement.v1\n" + request.ProviderCorrelationId));
        return new ExactModelProfileInferenceClientResolution(
            ExactModelProfileInferenceClientResolutionStatus.Resolved,
            new BrowserExactModelProfileLease(request.Primary, acknowledgement));
    }
}
