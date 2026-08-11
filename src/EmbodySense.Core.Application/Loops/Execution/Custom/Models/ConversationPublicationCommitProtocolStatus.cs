namespace EmbodySense.Core.Application.Loops.Execution.Custom.Models;

/// <summary>Describes whether one caller-owned publication boundary completed its append protocol.</summary>
public enum ConversationPublicationCommitProtocolStatus
{
    /// <summary>The callback ran exactly once, completed, and returned its exact result while the boundary was active.</summary>
    Completed,

    /// <summary>The boundary returned without invoking the callback.</summary>
    CallbackNotInvoked,

    /// <summary>The boundary invoked the callback more than once.</summary>
    CallbackInvokedMultipleTimes,

    /// <summary>The boundary returned before the callback completed.</summary>
    CallbackIncomplete,

    /// <summary>The callback failed or its failure was suppressed by the boundary.</summary>
    CallbackFailed,

    /// <summary>The boundary failed independently before or after the callback.</summary>
    BoundaryFailed
}
