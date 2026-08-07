namespace EmbodySense.Core.Application.Triggers.Models;

/// <summary>Defines the bounded caller-requested queue priority used only as a later selection input.</summary>
public enum TriggerQueuePriority
{
    /// <summary>Background priority.</summary>
    Background,

    /// <summary>Normal priority.</summary>
    Normal,

    /// <summary>Elevated priority.</summary>
    Elevated,

    /// <summary>Critical priority.</summary>
    Critical
}
