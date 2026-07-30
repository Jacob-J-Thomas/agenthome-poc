namespace EmbodySense.Core.Common.Governance.Audit;

public static partial class AuditSchema
{
    public static class Outcomes
    {
        public const string Started = "started";

        public const string Succeeded = "succeeded";

        public const string Failed = "failed";

        public const string Allowed = "allowed";

        public const string RequiresApproval = "requires_approval";

        public const string Denied = "denied";

        public const string Requested = "requested";

        public const string Approved = "approved";

        public const string Rejected = "rejected";

        public const string ApprovalRejected = "approval_rejected";

        public const string Unknown = "unknown";

        public const string Conflict = "conflict";

        public const string NotFound = "not_found";

        public const string NeedsReview = "needs_review";

        public const string CommittedWithAuditWarning = "committed_with_audit_warning";
    }
}
