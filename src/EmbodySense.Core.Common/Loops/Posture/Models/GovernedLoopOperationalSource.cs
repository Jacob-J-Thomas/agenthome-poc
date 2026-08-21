namespace EmbodySense.Core.Common.Loops.Posture.Models;

/// <summary>Identifies the authoritative local-background subsystem that supplied posture evidence.</summary>
public enum GovernedLoopOperationalSource
{
    /// <summary>Durable trigger queue and worker-delivery evidence.</summary>
    Queue = 1,

    /// <summary>Durable schedule definition and occurrence state.</summary>
    Schedule = 2,

    /// <summary>Durable sleeping-checkpoint and wake evidence.</summary>
    Wake = 3,

    /// <summary>Durable governed-run lifecycle and frontier evidence.</summary>
    Run = 4,

    /// <summary>Durable local coordinator ownership, heartbeat, and failure evidence.</summary>
    Coordinator = 5
}
