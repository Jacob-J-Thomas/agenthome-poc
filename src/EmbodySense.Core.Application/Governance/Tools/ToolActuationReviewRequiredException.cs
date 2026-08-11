using EmbodySense.Core.Application.Governance.Tools.Models;

namespace EmbodySense.Core.Application.Governance.Tools;

/// <summary>Signals that a tool actuator paused for review or ambiguous-authority reconciliation without committing.</summary>
public sealed class ToolActuationReviewRequiredException : Exception
{
    /// <summary>Creates a review-required failure from a non-direct authority disposition.</summary>
    /// <param name="disposition">The review-required or ambiguous disposition.</param>
    /// <param name="detail">The bounded operator-facing explanation.</param>
    public ToolActuationReviewRequiredException(ToolActuationAuthorityDisposition disposition, string detail)
        : base(detail)
    {
        if (disposition is not (ToolActuationAuthorityDisposition.ReviewRequired or ToolActuationAuthorityDisposition.Ambiguous))
        {
            throw new ArgumentOutOfRangeException(nameof(disposition), disposition, "Only review-required or ambiguous authority can create a review checkpoint exception.");
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(detail);
        Disposition = disposition;
    }

    /// <summary>Gets the authority disposition that requires operator attention.</summary>
    public ToolActuationAuthorityDisposition Disposition { get; }
}
