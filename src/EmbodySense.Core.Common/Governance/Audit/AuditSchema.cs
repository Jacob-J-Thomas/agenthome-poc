namespace EmbodySense.Core.Common.Governance.Audit;

public static class AuditSchema
{
    public static class Actors
    {
        public const string Cli = "embodysense.cli";

        public const string Web = "embodysense.web";

        public const string Llm = "embodysense.llm";

        public const string Tool = "embodysense.tool";
    }

    public static class Actions
    {
        public const string WorkspaceInit = "workspace.init";

        public const string LlmInferenceStart = "llm.inference.start";

        public const string LlmInferenceComplete = "llm.inference.complete";

        public const string LlmAppServerRequest = "llm.appserver.request";

        public const string LoopDefinitionMutationIntent = "loop.definition.mutation.intent";

        public const string LoopDefinitionMutationOutcome = "loop.definition.mutation.outcome";

        public const string LoopRunAdmission = "loop.run.admission";

        public const string LoopRunLifecycle = "loop.run.lifecycle";

        public const string LoopTraceDeletionIntent = "loop.trace.deletion.intent";

        public const string LoopTraceDeletionOutcome = "loop.trace.deletion.outcome";

        public const string LoopNodeAttempt = "loop.node.attempt";

        public const string LoopExitDecision = "loop.exit.decision";

        public const string ToolPermissionEvaluate = "tool.permission.evaluate";

        public const string ToolLoopAuthorityEvaluate = "tool.loop_authority.evaluate";

        public const string ToolApprovalRequest = "tool.approval.request";

        public const string ToolApprovalDecision = "tool.approval.decision";

        public const string ToolExecutionIntent = "tool.execution.intent";

        public const string ToolResponseRetain = "tool.response.retain";

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

        public const string Conflict = "conflict";

        public const string NotFound = "not_found";

        public const string NeedsReview = "needs_review";

        public const string CommittedWithAuditWarning = "committed_with_audit_warning";
    }
}
