using EmbodySense.Core.Application.Loops.ReceiptRetention.Models;
using EmbodySense.Core.Common.Loops.Models.Custom.Retention;

namespace EmbodySense.Core.Application.Loops.ReceiptRetention;

/// <summary>
/// Exposes one class-specific receipt-retention posture, lookup, and governed cleanup capability without adapter coupling.
/// </summary>
public interface ICustomLoopReceiptRetentionPort
{
    /// <summary>
    /// Gets the artifact class owned by this port.
    /// </summary>
    /// <value>The class-specific retention boundary.</value>
    CustomLoopReceiptArtifactClass ArtifactClass { get; }

    /// <summary>
    /// Inspects accounted usage, exact replay horizon, and fail-closed posture.
    /// </summary>
    /// <param name="cancellationToken">The token used to cancel inspection.</param>
    /// <returns>The current class posture.</returns>
    Task<CustomLoopReceiptClassPosture> InspectAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Inspects the safe accounting and durable state summary for the class's active cleanup journal.
    /// </summary>
    /// <param name="cancellationToken">The token used to cancel inspection.</param>
    /// <returns>The journal byte count and durable stage/outcome without exposing journal identity or candidates.</returns>
    Task<CustomLoopReceiptActiveCleanupJournalPosture> InspectActiveCleanupJournalAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Distinguishes an exact receipt, an expired compact proof, and a previously unseen operation identity.
    /// </summary>
    /// <param name="operationId">The operation identity.</param>
    /// <param name="cancellationToken">The token used to cancel lookup.</param>
    /// <returns>The exact, expired, or unknown lookup result.</returns>
    Task<CustomLoopReceiptOperationLookupResult> LookupOperationAsync(string operationId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Executes no more than the caller's bounded, validated, governed cleanup command.
    /// </summary>
    /// <param name="command">The timestamp-free bounded cleanup command.</param>
    /// <param name="cancellationToken">The token used to cancel cleanup.</param>
    /// <returns>The committed, blocked, exhausted, conflicted, or invalid result.</returns>
    Task<CustomLoopReceiptCleanupResult> CleanupAsync(CustomLoopReceiptCleanupCommand command, CancellationToken cancellationToken = default);
}
