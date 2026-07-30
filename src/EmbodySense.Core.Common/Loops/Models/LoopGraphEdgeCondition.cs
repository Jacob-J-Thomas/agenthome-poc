namespace EmbodySense.Core.Common.Loops.Models;

/// <summary>
/// Identifies the supported loop graph edge condition values.
/// </summary>
public enum LoopGraphEdgeCondition
{
    /// <summary>
    /// Identifies the unknown loop graph edge condition.
    /// </summary>
    Unknown = 0,
    /// <summary>
    /// Identifies the always loop graph edge condition.
    /// </summary>
    Always,
    /// <summary>
    /// Identifies the success loop graph edge condition.
    /// </summary>
    Success,
    /// <summary>
    /// Identifies the failure loop graph edge condition.
    /// </summary>
    Failure,
    /// <summary>
    /// Identifies the cancellation loop graph edge condition.
    /// </summary>
    Cancellation,
    /// <summary>
    /// Identifies the authority boundary loop graph edge condition.
    /// </summary>
    AuthorityBoundary
}
