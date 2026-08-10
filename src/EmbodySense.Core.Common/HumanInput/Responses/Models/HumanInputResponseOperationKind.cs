namespace EmbodySense.Core.Common.HumanInput.Responses.Models;

/// <summary>Identifies one explicit durable authenticated-response operation.</summary>
public enum HumanInputResponseOperationKind
{
    /// <summary>No supported operation was supplied.</summary>
    Unknown = 0,
    /// <summary>Submit one new immutable response artifact.</summary>
    Submit = 1,
    /// <summary>Withdraw one exact retained response without deleting its evidence.</summary>
    Withdraw = 2,
    /// <summary>Explicitly select retained responses under a manual-selection policy.</summary>
    Select = 3
}
