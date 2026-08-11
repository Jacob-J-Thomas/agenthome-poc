namespace EmbodySense.Core.Application.Governance.Tools.Models;

/// <summary>Identifies the terminal current-authority disposition for one tool actuation attempt.</summary>
public enum ToolActuationAuthorityDisposition
{
    /// <summary>The actuator committed directly while the current authority boundary was held.</summary>
    Direct = 0,

    /// <summary>Current authority definitively denied the actuator.</summary>
    Denied = 1,

    /// <summary>The actuator requires an explicit human-review checkpoint before it may run.</summary>
    ReviewRequired = 2,

    /// <summary>Authority evidence was ambiguous, so an operator must reconcile it before the actuator may run.</summary>
    Ambiguous = 3
}
