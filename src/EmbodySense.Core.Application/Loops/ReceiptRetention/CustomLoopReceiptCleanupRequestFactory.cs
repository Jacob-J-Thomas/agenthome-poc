using EmbodySense.Core.Application.Loops.ReceiptRetention.Models;
using EmbodySense.Core.Common.Loops.Custom.Retention;

namespace EmbodySense.Core.Application.Loops.ReceiptRetention;

/// <summary>
/// Creates persisted cleanup requests from caller-safe commands and an adapter-owned trusted time observation.
/// </summary>
public static class CustomLoopReceiptCleanupRequestFactory
{
    /// <summary>
    /// Creates the canonical persisted request for a validated cleanup command.
    /// </summary>
    /// <param name="command">The caller-supplied timestamp-free cleanup command.</param>
    /// <param name="trustedObservedAtUtc">The current UTC time observed from the adapter's trusted <see cref="TimeProvider"/>.</param>
    /// <returns>The canonical request that may be persisted in a cleanup journal.</returns>
    public static CustomLoopReceiptCleanupRequest Create(CustomLoopReceiptCleanupCommand command, DateTimeOffset trustedObservedAtUtc)
    {
        CustomLoopReceiptRetentionContractValidator.ValidateCleanupCommand(command);
        var request = new CustomLoopReceiptCleanupRequest(
            CustomLoopReceiptCleanupRequest.CurrentSchemaVersion,
            command.ArtifactClass,
            command.OperationId,
            command.Actor,
            command.Surface,
            trustedObservedAtUtc,
            CustomLoopReceiptRetentionPolicy.GetReplayCutoffUtc(trustedObservedAtUtc),
            command.MaximumArtifactCount,
            command.MaximumArtifactUtf8Bytes);
        CustomLoopReceiptRetentionContractValidator.ValidateCleanupRequest(request);
        return request;
    }
}
