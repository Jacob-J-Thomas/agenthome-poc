using EmbodySense.Core.Common.Inference.Profiles.Models;
using EmbodySense.Core.Application.Governance.Tools;

namespace EmbodySense.Core.Application.Inference.Profiles.Models;

/// <summary>Supplies an exact non-secret profile/attempt/authority/budget envelope to Startup adapter resolution.</summary>
public sealed record ExactModelProfileInferenceClientRequest(
    GovernedModelProfilePin Primary,
    GovernedModelUsageLedgerIdentity AttemptIdentity,
    GovernedModelUsageCeiling Reservation,
    GovernedModelBudgetPolicy BudgetPolicy,
    string RoutingAdmissionHash,
    string AdmissionReceiptHash,
    string AuthorityEvidenceHash,
    string DataPostureEvidenceHash,
    string ProviderAttemptId,
    string ProviderCorrelationId,
    IToolBroker? ToolBroker = null);
