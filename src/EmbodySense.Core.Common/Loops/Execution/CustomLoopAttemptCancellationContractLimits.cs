namespace EmbodySense.Core.Common.Loops.Execution;

/// <summary>Defines bounded routing limits for active-attempt cancellation.</summary>
public static class CustomLoopAttemptCancellationContractLimits
{
    /// <summary>
    /// The maximum end-to-end seconds allowed for a remote cancellation route, including connection, request,
    /// acknowledgement, and completion-scheduling windows.
    /// </summary>
    public const int MaxRemoteRequestSeconds = 10;
}
