using System.Text.Json.Serialization;
using EmbodySense.Core.Application.Loops.Models;
using EmbodySense.Core.Common.Loops.Models.Custom.Retention;

namespace EmbodySense.Core.Application.Loops.ReceiptRetention.Models;

/// <summary>
/// Represents compact proof that an idempotency identity existed after its exact receipt expired.
/// </summary>
/// <param name="SchemaVersion">The proof schema version.</param>
/// <param name="ArtifactClass">The source receipt artifact class.</param>
/// <param name="DefinitionMutationKind">The definition mutation kind when the source is a definition-mutation receipt; otherwise <see langword="null"/>.</param>
/// <param name="OperationId">The original operation identity.</param>
/// <param name="RequestHash">The canonical original request hash.</param>
/// <param name="OutcomeHash">The canonical terminal outcome hash.</param>
/// <param name="CompletedAtUtc">The terminal completion timestamp.</param>
/// <param name="ExpiredAtUtc">The timestamp at which exact replay expired.</param>
public sealed record CustomLoopExpiredOperationProof(
    int SchemaVersion,
    CustomLoopReceiptArtifactClass ArtifactClass,
    [property: JsonRequired] CustomLoopDefinitionMutationKind? DefinitionMutationKind,
    string OperationId,
    string RequestHash,
    string OutcomeHash,
    DateTimeOffset CompletedAtUtc,
    DateTimeOffset ExpiredAtUtc)
{
    /// <summary>
    /// Current compact expired-operation proof schema version.
    /// </summary>
    public const int CurrentSchemaVersion = 1;
}
