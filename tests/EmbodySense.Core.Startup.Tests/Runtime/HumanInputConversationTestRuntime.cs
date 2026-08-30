using EmbodySense.Core.Application.Capabilities;
using EmbodySense.Core.Application.HumanInput.Lifecycle.Models;
using EmbodySense.Core.Common.HumanInput;
using EmbodySense.Core.Common.HumanInput.Lifecycle.Models;
using EmbodySense.Core.Common.HumanInput.Models;
using EmbodySense.Core.Persistence.Tests.HumanInput.Requests;
using EmbodySense.Core.Startup.Runtime;
using EmbodySense.Core.Startup.Runtime.Models;
using EmbodySense.Core.Startup.Workspace;
using EmbodySense.Tests.Support;

namespace EmbodySense.Core.Startup.Tests.Runtime;

internal static class HumanInputConversationTestRuntime
{
    internal static async Task<AgentRuntime> CreateAsync(TestWorkspace workspace)
    {
        var executablePath = workspace.File(OperatingSystem.IsWindows() ? "human-input-codex.cmd" : "human-input-codex");
        await File.WriteAllTextAsync(executablePath, OperatingSystem.IsWindows() ? "@exit /b 0\r\n" : "#!/bin/sh\nexit 0\n");
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(executablePath, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }

        var status = new CodexRuntimeStatus(
            CodexRuntimeCompatibility.Compatible,
            executablePath,
            Path.GetFullPath(executablePath),
            "codex-cli 999.0.0-test",
            "test-model",
            "controlled test",
            "The isolated Human Input command test does not dispatch model turns.");
        return await AgentRuntimeFactory.ForFileCapabilityTrustRoot(new HumanInputConversationRejectingApprovalPrompt(), workspace.ServerStatePath, status).CreateAsync(
            "test-model",
            workspace.RootPath,
            executablePath,
            "read-only",
            AgentRuntimeSurface.Cli);
    }

    internal static HumanInputRequestLifecycleStoreMutation CreateRequest(
        string workspacePath,
        string requestId,
        string requestVersionId,
        string operationId,
        long generation,
        HumanInputResponseSchema schema,
        HumanInputResponsePolicy? policy = null,
        string purpose = "Collect display-safe test data.",
        string prompt = "Provide display-safe test data.")
    {
        var requestedAtUtc = DateTimeOffset.UtcNow.AddMinutes(-1).ToUniversalTime();
        var binding = new HumanInputRequestBinding(
            CapabilityWorkspaceScopeId.Create(workspacePath),
            "governed-loop",
            "loop-revision-one",
            "node-one",
            $"run-{requestId}",
            $"checkpoint-{requestId}");
        var respondents = policy?.Kind == HumanInputResponsePolicyKind.Quorum
            ? new[]
            {
                new HumanInputEligibleRespondent(WorkspaceActors.Cli, "cli-respondent", "route-cli"),
                new HumanInputEligibleRespondent("user-two", "second-respondent", "route-two")
            }
            : [new HumanInputEligibleRespondent(WorkspaceActors.Cli, "cli-respondent", "route-cli")];
        var request = HumanInputRequestStoreTestData.Rehash(new HumanInputRequest(
            HumanInputRequest.CurrentSchemaVersion,
            requestId,
            requestVersionId,
            binding,
            purpose,
            prompt,
            schema,
            HumanInputPrivacyClass.Sensitive,
            respondents,
            new HumanInputTiming(requestedAtUtc, requestedAtUtc.AddHours(1)),
            policy ?? new HumanInputResponsePolicy(HumanInputResponsePolicyKind.FirstValid, null, null),
            new HumanInputContinuationBinding(HumanInputContinuationPolicyKind.BoundNodeAndCheckpointOnly, binding.NodeId, binding.CheckpointId),
            string.Empty));
        var head = HumanInputRequestStoreTestData.Head(request, 1, HumanInputRequestLifecycleStatus.Pending, 0, null, null, operationId, requestedAtUtc);
        var evidence = HumanInputRequestStoreTestData.Evidence(
            HumanInputRequestLifecycleOperationKind.Create,
            requestId,
            operationId,
            HumanInputRequestStoreTestData.HashA,
            requestedAtUtc,
            null,
            head,
            request);
        return new HumanInputRequestLifecycleStoreMutation(generation, evidence, request, head, null);
    }

    internal static string SubmitCommand(
        HumanInputRequestLifecycleHead head,
        string operationId,
        string responseId,
        string payload)
        => $"/human-input submit {Terms(head)} {operationId} {responseId} {payload}";

    internal static string TargetCommand(
        string command,
        HumanInputRequestLifecycleHead head,
        string operationId,
        string responseId)
        => $"/human-input {command} {Terms(head)} {operationId} {responseId}";

    internal static string Terms(HumanInputRequestLifecycleHead head)
        => $"{head.RequestId} {head.LifecycleVersion} {head.Status.ToString().ToLowerInvariant()} {head.CurrentRequest.RequestVersionId} {head.CurrentRequest.RequestHash}";
}
