namespace EmbodySense.Core.Application.HumanInput.Responses.Models;

/// <summary>Identifies one safe response-operation result.</summary>
public enum HumanInputResponseLifecycleMutationStatus
{
    /// <summary>No supported result was supplied.</summary>
    Unknown = 0,
    /// <summary>The exact operation committed durably.</summary>
    Committed = 1,
    /// <summary>The exact previously committed operation was replayed.</summary>
    Replayed = 2,
    /// <summary>The command envelope or typed response value was invalid.</summary>
    Invalid = 3,
    /// <summary>The current caller could not be authenticated.</summary>
    Denied = 4,
    /// <summary>The authenticated actor was not eligible for the requested operation.</summary>
    Ineligible = 5,
    /// <summary>Exact request, response, policy, or optimistic state conflicted.</summary>
    Conflict = 6,
    /// <summary>The exact request or targeted response did not exist.</summary>
    NotFound = 7,
    /// <summary>The trusted submission or selection endpoint had passed.</summary>
    Late = 8,
    /// <summary>A finite schema-1 bound was exhausted.</summary>
    LimitExceeded = 9,
    /// <summary>The service or store could not establish a durable result.</summary>
    Unavailable = 10,
    /// <summary>Available evidence cannot establish one safe result.</summary>
    Ambiguous = 11,
}
