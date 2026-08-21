using EmbodySense.Core.Application.CommandActions;
using EmbodySense.Core.Common.CommandActions.Models;

namespace EmbodySense.Core.Clients.Tests.CommandActions;

internal sealed class InMemoryCommandActionEvidenceStore : ICommandActionEvidenceStore
{
    internal List<CommandActionPreparationEvidence> Preparations { get; } = [];
    internal List<CommandActionOutcomeEvidence> Outcomes { get; } = [];

    public Task RetainPreparationAsync(CommandActionPreparationEvidence evidence, CancellationToken cancellationToken = default)
    {
        Preparations.Add(evidence);
        return Task.CompletedTask;
    }

    public Task<CommandActionPreparationEvidence?> ReadPreparationAsync(string evidenceId, CancellationToken cancellationToken = default)
        => Task.FromResult(Preparations.SingleOrDefault(candidate => string.Equals(candidate.EvidenceId, evidenceId, StringComparison.Ordinal)));

    public Task RetainOutcomeAsync(CommandActionOutcomeEvidence evidence, CancellationToken cancellationToken = default)
    {
        Outcomes.Add(evidence);
        return Task.CompletedTask;
    }

    public Task<CommandActionOutcomeEvidence?> ReadOutcomeAsync(string evidenceId, CancellationToken cancellationToken = default)
        => Task.FromResult(Outcomes.SingleOrDefault(candidate => string.Equals(candidate.EvidenceId, evidenceId, StringComparison.Ordinal)));

    public Task<CommandActionOutcomeEvidence?> ReadOutcomeByOperationAsync(string idempotencyOperationId, long effectGeneration, CancellationToken cancellationToken = default)
        => Task.FromResult(Outcomes.SingleOrDefault(candidate => string.Equals(candidate.IdempotencyOperationId, idempotencyOperationId, StringComparison.Ordinal) && candidate.EffectGeneration == effectGeneration));
}
