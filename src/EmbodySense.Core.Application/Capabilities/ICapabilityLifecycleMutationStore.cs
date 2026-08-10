using EmbodySense.Core.Application.Capabilities.Models;

namespace EmbodySense.Core.Application.Capabilities;

/// <summary>Owns one authenticated lifecycle aggregate, mutation lock, history, degradation evidence, and idempotency ledger.</summary>
public interface ICapabilityLifecycleMutationStore
{
    /// <summary>Reads current or last-proved state, immutable history, and optional degradation evidence.</summary>
    Task<CapabilityLifecycleReadResult> ReadAsync(EmbodySense.Core.Common.Capabilities.CapabilityId capabilityId, CancellationToken cancellationToken = default);

    /// <summary>Persists and returns a deterministic preview over one exact dependent snapshot.</summary>
    Task<CapabilityLifecyclePreview> PreviewAsync(CapabilityLifecyclePreviewRequest request, CapabilityLifecycleBaseline? baseline, CapabilityDependentIndexSnapshot dependents, CancellationToken cancellationToken = default);

    /// <summary>Returns a persisted preview only when the exact server-validated selection identity was already admitted.</summary>
    Task<CapabilityLifecyclePreview> TryReplaySelectionAsync(CapabilityLifecycleSelectionRequest request, CancellationToken cancellationToken = default);

    /// <summary>Applies or rejects the exact preview after fresh baseline and dependent recapture.</summary>
    Task<CapabilityLifecycleMutationResult> MutateAsync(CapabilityLifecyclePreview preview, CapabilityLifecycleBaseline? baseline, CapabilityDependentIndexSnapshot dependents, CancellationToken cancellationToken = default);

    /// <summary>Durably retires one exact unresolved preview without applying its transition.</summary>
    Task<CapabilityLifecycleMutationResult> DiscardAsync(CapabilityLifecyclePreview preview, CancellationToken cancellationToken = default);

    /// <summary>Marks a terminal operation receipt after its final audit event is durable.</summary>
    Task<CapabilityLifecycleAuditMarkStatus> MarkOutcomeAuditedAsync(string operationId, CancellationToken cancellationToken = default);
}
