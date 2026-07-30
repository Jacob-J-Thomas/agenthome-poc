namespace EmbodySense.Core.Application.Loops.Models;

/// <summary>
/// Identifies the supported custom loop create operation lookup status values.
/// </summary>
public enum CustomLoopCreateOperationLookupStatus
{
    /// <summary>
    /// Identifies the not found custom loop create operation lookup status.
    /// </summary>
    NotFound = 1,
    /// <summary>
    /// Identifies the pending definition commit custom loop create operation lookup status.
    /// </summary>
    PendingDefinitionCommit = 2,
    /// <summary>
    /// Identifies the committed custom loop create operation lookup status.
    /// </summary>
    Committed = 3
}
