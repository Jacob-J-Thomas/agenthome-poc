namespace EmbodySense.Core.Application.Loops.ReceiptRetention;

public static class CustomLoopInvocationReceiptRetentionPolicy
{
    public static readonly TimeSpan MinimumReplayDuration = TimeSpan.FromDays(30);

    public static readonly TimeSpan OperationOwnershipWindow = TimeSpan.FromSeconds(30);
}
