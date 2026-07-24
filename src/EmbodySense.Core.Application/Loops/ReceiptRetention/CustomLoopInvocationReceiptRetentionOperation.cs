namespace EmbodySense.Core.Application.Loops.ReceiptRetention;

public sealed record CustomLoopInvocationReceiptRetentionOperation(
    int SchemaVersion,
    string OperationId,
    string Actor,
    string Surface,
    DateTimeOffset RequestedAtUtc,
    DateTimeOffset ReplayCutoffUtc,
    DateTimeOffset UpdatedAtUtc,
    CustomLoopInvocationReceiptRetentionCandidate[] Candidates,
    CustomLoopInvocationReceiptRetentionOperationState State,
    int DeletedReceiptCount,
    long DeletedReceiptUtf8Bytes)
{
    public const int CurrentSchemaVersion = 1;
}
