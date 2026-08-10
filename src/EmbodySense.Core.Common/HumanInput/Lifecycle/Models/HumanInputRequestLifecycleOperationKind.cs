namespace EmbodySense.Core.Common.HumanInput.Lifecycle.Models;

/// <summary>Identifies one explicit durable Human Input request lifecycle operation.</summary>
public enum HumanInputRequestLifecycleOperationKind
{
    /// <summary>No supported operation was supplied.</summary>
    Unknown = 0,
    /// <summary>Create one new pending request.</summary>
    Create = 1,
    /// <summary>Record another delivery opportunity for the exact current request.</summary>
    Remind = 2,
    /// <summary>Replace only the eligible respondent routing on a new immutable request version.</summary>
    Reroute = 3,
    /// <summary>Replace bounded request content on a new immutable request version without changing routing or binding.</summary>
    Amend = 4,
    /// <summary>Terminally reject the pending request without treating the decision as response data or approval.</summary>
    Reject = 5,
    /// <summary>Terminally cancel the pending request.</summary>
    Cancel = 6,
    /// <summary>Terminally expire the pending request after its inclusive response endpoint.</summary>
    Expire = 7,
    /// <summary>Atomically replace the pending request with a different linked request.</summary>
    Supersede = 8
}
