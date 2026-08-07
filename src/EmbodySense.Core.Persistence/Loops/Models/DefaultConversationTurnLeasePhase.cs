namespace EmbodySense.Core.Persistence.Loops.Models;

/// <summary>Identifies a public coordination point in active-set lease acquisition.</summary>
public enum DefaultConversationTurnLeasePhase
{
    /// <summary>The no-follow lease handle has valid owner-only posture and has not yet been locked.</summary>
    AfterValidatedOpenBeforeExclusiveLock,

    /// <summary>The lease handle owns the OS-exclusive lock and is about to undergo final posture and pathname validation.</summary>
    AfterExclusiveLockBeforeFinalValidation
}
