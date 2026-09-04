namespace EmbodySense.Core.Startup.HumanInput.Models;

/// <summary>Identifies the fail-closed posture of the server-owned route-intent source.</summary>
public enum HumanInputRouteIntentSourceStatus
{
    /// <summary>No supported source posture was supplied.</summary>
    Unknown = 0,

    /// <summary>The canonical route intents were resolved and integrity checked.</summary>
    Ready = 1,

    /// <summary>The canonical request or source result was malformed.</summary>
    Invalid = 2,

    /// <summary>A trusted dependency was unavailable.</summary>
    Unavailable = 3,

    /// <summary>The source could not establish one consistent result.</summary>
    Ambiguous = 4
}
