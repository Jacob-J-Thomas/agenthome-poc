namespace EmbodySense.Web.Models;

/// <summary>Projects the non-sensitive process posture of canonical governed-loop background delivery.</summary>
public enum WebGovernedLoopBackgroundPosture
{
    /// <summary>The Web process owns a coordinator that can admit bounded background work.</summary>
    Ready = 1,

    /// <summary>Background delivery remains safe but this Web process is not the active owner.</summary>
    Degraded = 2,

    /// <summary>The process cannot safely establish or inspect canonical background delivery.</summary>
    Unavailable = 3,

    /// <summary>New work admission has stopped while retained work reaches a durable safe boundary.</summary>
    Draining = 4,

    /// <summary>No active local coordinator remains for this Web process.</summary>
    Stopped = 5,
}
