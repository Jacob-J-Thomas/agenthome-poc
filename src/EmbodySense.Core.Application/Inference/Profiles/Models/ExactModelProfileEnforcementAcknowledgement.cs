namespace EmbodySense.Core.Application.Inference.Profiles.Models;

using EmbodySense.Core.Common.Inference.Models;

/// <summary>Affirmatively binds adapter-applied pre-dispatch hard bounds without secret/private configuration.</summary>
public sealed record ExactModelProfileEnforcementAcknowledgement(
    string ProfilePinHash,
    string AttemptIdentityHash,
    string ReservationHash,
    string BudgetPolicyHash,
    string RoutingAdmissionHash,
    string AdmissionReceiptHash,
    string AuthorityEvidenceHash,
    string DataPostureEvidenceHash,
    string ExpectedProviderId,
    LlmInferenceSurface ExpectedResponseSurface,
    string ProviderAttemptId,
    string ProviderCorrelationId,
    string EnforcementEvidenceHash);
