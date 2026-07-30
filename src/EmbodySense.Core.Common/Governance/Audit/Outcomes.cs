namespace EmbodySense.Core.Common.Governance.Audit;

public static partial class AuditSchema
{
    /// <summary>
    /// Defines canonical audit outcome identifiers.
    /// </summary>
    public static class Outcomes
    {
        /// <summary>
        /// Identifies the started audit outcome.
        /// </summary>
        public const string Started = "started";

        /// <summary>
        /// Identifies the succeeded audit outcome.
        /// </summary>
        public const string Succeeded = "succeeded";

        /// <summary>
        /// Identifies the failed audit outcome.
        /// </summary>
        public const string Failed = "failed";

        /// <summary>
        /// Identifies the allowed audit outcome.
        /// </summary>
        public const string Allowed = "allowed";

        /// <summary>
        /// Identifies the requires approval audit outcome.
        /// </summary>
        public const string RequiresApproval = "requires_approval";

        /// <summary>
        /// Identifies the denied audit outcome.
        /// </summary>
        public const string Denied = "denied";

        /// <summary>
        /// Identifies the requested audit outcome.
        /// </summary>
        public const string Requested = "requested";

        /// <summary>
        /// Identifies the approved audit outcome.
        /// </summary>
        public const string Approved = "approved";

        /// <summary>
        /// Identifies the rejected audit outcome.
        /// </summary>
        public const string Rejected = "rejected";

        /// <summary>
        /// Identifies the approval rejected audit outcome.
        /// </summary>
        public const string ApprovalRejected = "approval_rejected";

        /// <summary>
        /// Identifies the unknown audit outcome.
        /// </summary>
        public const string Unknown = "unknown";

        /// <summary>
        /// Identifies the conflict audit outcome.
        /// </summary>
        public const string Conflict = "conflict";

        /// <summary>
        /// Identifies the not found audit outcome.
        /// </summary>
        public const string NotFound = "not_found";

        /// <summary>
        /// Identifies the needs review audit outcome.
        /// </summary>
        public const string NeedsReview = "needs_review";

        /// <summary>
        /// Identifies the committed with audit warning audit outcome.
        /// </summary>
        public const string CommittedWithAuditWarning = "committed_with_audit_warning";
    }
}
