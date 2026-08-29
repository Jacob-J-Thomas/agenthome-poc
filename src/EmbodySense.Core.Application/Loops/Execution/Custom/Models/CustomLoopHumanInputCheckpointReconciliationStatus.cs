namespace EmbodySense.Core.Application.Loops.Execution.Custom.Models;

/// <summary>Identifies the fail-closed disposition of one canonical pending Human Input checkpoint during loop cancellation.</summary>
internal enum CustomLoopHumanInputCheckpointReconciliationStatus
{
    Advanced,
    Pending,
    Blocked,
    Conflict,
    Unavailable,
    Corrupt,
}
