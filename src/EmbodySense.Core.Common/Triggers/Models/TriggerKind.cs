namespace EmbodySense.Core.Common.Triggers.Models;

/// <summary>
/// Identifies the closed source category of a trigger observation.
/// </summary>
public enum TriggerKind
{
    /// <summary>The trigger kind is absent or unsupported.</summary>
    Unknown = 0,
    /// <summary>A human initiated the trigger.</summary>
    Human = 1,
    /// <summary>A webhook observation initiated the trigger.</summary>
    Webhook = 2,
    /// <summary>A file-change observation initiated the trigger.</summary>
    FileChange = 3,
    /// <summary>A message observation initiated the trigger.</summary>
    Message = 4,
    /// <summary>A time observation initiated the trigger.</summary>
    Time = 5,
    /// <summary>A tool result initiated the trigger.</summary>
    ToolOutput = 6,
    /// <summary>Another governed loop initiated the trigger.</summary>
    Loop = 7,
    /// <summary>A harness system event initiated the trigger.</summary>
    System = 8,
    /// <summary>A monitored condition initiated the trigger.</summary>
    MonitoredCondition = 9
}
