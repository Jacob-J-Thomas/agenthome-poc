using System.Globalization;
using EmbodySense.Core.Application.Inference.Profiles.Models;
using EmbodySense.Core.Common.Capabilities;

namespace EmbodySense.Core.Application.Inference.Profiles;

/// <summary>Classifies every exact provider payload at the conservative server-owned sensitive boundary.</summary>
/// <remarks>Content text is never inspected, inferred as public, or retained; the exact payload hash binds the evidence.</remarks>
public sealed class ConservativeModelInferenceDataPostureSource : IModelInferenceDataPostureSource
{
    private static readonly CapabilityDataClass _sensitive = CreateSensitive();

    /// <inheritdoc />
    public Task<ModelInferenceDataPosture> ReadAsync(
        ModelInferenceDataPostureRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(request.Attempt);
        ArgumentNullException.ThrowIfNull(request.InferenceRequest);
        cancellationToken.ThrowIfCancellationRequested();
        var attempt = request.Attempt;
        var evidence = GovernedModelAttemptEvidenceHash.Create(
            "embodysense.model-attempt-sensitive-input-posture.v1",
            request.InputPayloadHash,
            attempt.RoutingAdmission.ContentHash,
            attempt.AdmissionReceipt.ContentHash,
            attempt.RunId,
            attempt.ExecutionGeneration.ToString(CultureInfo.InvariantCulture),
            attempt.NodeId,
            attempt.PlanOrdinal.ToString(CultureInfo.InvariantCulture),
            attempt.ActivationOrdinal.ToString(CultureInfo.InvariantCulture),
            attempt.VisitOrdinal.ToString(CultureInfo.InvariantCulture),
            attempt.AttemptNumber.ToString(CultureInfo.InvariantCulture),
            attempt.AttemptOperationId,
            _sensitive.Value);
        return Task.FromResult(new ModelInferenceDataPosture(
            ModelInferenceDataPostureStatus.Available,
            attempt.RunId,
            attempt.NodeId,
            attempt.PlanOrdinal,
            attempt.ActivationOrdinal,
            attempt.VisitOrdinal,
            attempt.AttemptNumber,
            attempt.AttemptOperationId,
            request.InputPayloadHash,
            [_sensitive],
            evidence));
    }

    private static CapabilityDataClass CreateSensitive()
    {
        _ = CapabilityDataClass.TryParse("sensitive", out var value, out _);
        return value!;
    }
}
