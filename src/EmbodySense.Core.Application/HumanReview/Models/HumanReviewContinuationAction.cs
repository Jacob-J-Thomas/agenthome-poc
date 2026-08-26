namespace EmbodySense.Core.Application.HumanReview.Models;

/// <summary>Identifies the declared non-adapter action that a validated Human Review decision may request.</summary>
public enum HumanReviewContinuationAction
{
    /// <summary>No action is safe or applicable.</summary>
    None = 0,

    /// <summary>Release the exact non-effect continuation at its parked frontier.</summary>
    ReleaseContinuation = 1,

    /// <summary>Release only the exact effect attempt proved not dispatched; the effect boundary must independently revalidate immediately before dispatch.</summary>
    ReleaseEffect = 2,

    /// <summary>Route a rejected terminal decision through the graph's authored failure path.</summary>
    FailRejected = 3,

    /// <summary>Route a cancelled terminal decision through canonical cancellation.</summary>
    Cancel = 4,

    /// <summary>Keep the exact ReviewBlocked frontier parked for a request-information decision.</summary>
    ParkForInformation = 5,
}
