namespace EmbodySense.Core.Common.HumanReview.Models;

/// <summary>Identifies the trusted component category that observed or recorded a Human Review artifact.</summary>
public enum HumanReviewProvenanceKind
{
    /// <summary>No supported provenance source was supplied.</summary>
    Unknown = 0,
    /// <summary>A trusted server admission, persistence, or lifecycle component recorded the artifact.</summary>
    Server = 1,
    /// <summary>An authenticated reviewer submission was observed without treating its claims as authority.</summary>
    AuthenticatedReviewer = 2,
    /// <summary>A durable coordinator or wake owner recorded the artifact.</summary>
    Coordinator = 3
}
