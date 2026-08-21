using EmbodySense.Core.Common.CommandActions.Models;

namespace EmbodySense.Core.Application.CommandActions;

/// <summary>Retains immutable command preparation and conclusive redacted outcome evidence.</summary>
public interface ICommandActionEvidenceStore
{
    /// <summary>Retains or exactly replays one content-addressed preparation record.</summary>
    Task RetainPreparationAsync(CommandActionPreparationEvidence evidence, CancellationToken cancellationToken = default);

    /// <summary>Reads and authenticates one preparation record.</summary>
    Task<CommandActionPreparationEvidence?> ReadPreparationAsync(string evidenceId, CancellationToken cancellationToken = default);

    /// <summary>Retains or exactly replays one content-addressed outcome record.</summary>
    Task RetainOutcomeAsync(CommandActionOutcomeEvidence evidence, CancellationToken cancellationToken = default);

    /// <summary>Reads and authenticates one outcome record.</summary>
    Task<CommandActionOutcomeEvidence?> ReadOutcomeAsync(string evidenceId, CancellationToken cancellationToken = default);

    /// <summary>Reads the one retained outcome for an exact stable operation generation, when one exists.</summary>
    Task<CommandActionOutcomeEvidence?> ReadOutcomeByOperationAsync(
        string idempotencyOperationId,
        long effectGeneration,
        CancellationToken cancellationToken = default);
}
