using EmbodySense.Core.Common.HumanInput.Responses.Models;

namespace EmbodySense.Core.Application.HumanInput.Responses.Models;

/// <summary>Associates one retained response-operation observation with the durable snapshot used to prove its causal chronology.</summary>
/// <param name="Evidence">The immutable terminal response-operation evidence.</param>
/// <param name="Snapshot">The durable response snapshot used to prove the observation, or null only for request-not-found evidence.</param>
public sealed partial record HumanInputResponseOperationCausalityObservation(
    HumanInputResponseOperationEvidence Evidence,
    HumanInputResponseLifecycleStoreSnapshot? Snapshot);
