namespace EmbodySense.Core.Startup.Runtime.Models;

/// <summary>Describes the process-local readiness of canonical governed-loop background delivery.</summary>
public enum AgentRuntimeGovernedLoopBackgroundReadiness
{
    /// <summary>This runtime owns an active coordinator that can admit bounded background work.</summary>
    Ready = 1,

    /// <summary>Durable background work remains safe, but this runtime cannot currently admit it as the active owner.</summary>
    Degraded = 2,

    /// <summary>The runtime cannot safely establish or inspect canonical background delivery.</summary>
    Unavailable = 3,

    /// <summary>New work admission has stopped while an owned work item drains to a durable safe boundary.</summary>
    Draining = 4,

    /// <summary>No active local background coordinator remains for this runtime.</summary>
    Stopped = 5,
}
