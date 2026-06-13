namespace EmbodySense.Core.Audit;

public static class AuditSchema
{
    public static class Actors
    {
        public const string Cli = "embodysense.cli";

        public const string Llm = "embodysense.llm";

        public const string Tool = "embodysense.tool";
    }

    public static class Actions
    {
        public const string WorkspaceInit = "workspace.init";

        public const string LlmInferenceStart = "llm.inference.start";

        public const string LlmInferenceComplete = "llm.inference.complete";

        public const string LlmAppServerRequest = "llm.appserver.request";

        public const string ToolPermissionEvaluate = "tool.permission.evaluate";

        public const string ToolApprovalRequest = "tool.approval.request";

        public const string ToolApprovalDecision = "tool.approval.decision";

        public const string ToolExecute = "tool.execute";
    }

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
    }
}
