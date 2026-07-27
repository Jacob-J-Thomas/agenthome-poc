namespace EmbodySense.Core.Application.Loops.Execution.Custom;

public enum CustomLoopAttemptCancellationStatus
{
    Unknown = 0,
    ProviderInterruptionConfirmed = 1,
    SignalDelivered = 2,
    NoActiveAttempt = 3,
    OwnerUnavailable = 4,
    Invalid = 5
}
