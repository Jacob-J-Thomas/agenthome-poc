using EmbodySense.Core.Common.Inference;
using EmbodySense.Core.Application.Governance.Audit;
using EmbodySense.Core.Application.Governance.Tools;
using EmbodySense.Core.Application.Governance.Tools.Models;
using EmbodySense.Core.Common.Governance.Audit;
using EmbodySense.Core.Common.Governance.Tools;
using EmbodySense.Core.Common.Governance.Tools.Models;
using EmbodySense.Core.Common.Inference.Models;
using EmbodySense.Core.Application.Loops.EffectAuthorityEvidence.Models;
using EmbodySense.Core.Application.Loops.Execution.Authority;
using EmbodySense.Core.Application.Loops.Execution.Authority.Models;
using EmbodySense.Core.Clients.CodexAppServer;
using EmbodySense.Core.Persistence.Audit;
using EmbodySense.Core.Common.Workspace;
using EmbodySense.Core.Startup.Inference;
using EmbodySense.Tests.Support;

namespace EmbodySense.Core.Startup.Tests.Inference;

public sealed class LlmInferenceClientTests
{
    [Fact]
    public async Task Governed_overload_rejects_a_null_boundary_before_audit_or_provider_activity()
    {
        using var workspace = new TestWorkspace();
        var paths = new WorkspacePaths(workspace.RootPath);
        await PrepareAuditWorkspaceAsync(paths);
        var auditBefore = await File.ReadAllTextAsync(paths.EventsLogPath);
        var transport = new RecordingCodexAppServerTransport();
        await using var client = CreateCodexClient(workspace, transport);

        var exception = await Assert.ThrowsAsync<ArgumentNullException>(() => client.GenerateAsync(
            LlmInferenceRequest.FromUserText("hello"),
            responseChunkHandler: null,
            CancellationToken.None,
            providerTransportCommitBoundary: null!));

        Assert.Equal("providerTransportCommitBoundary", exception.ParamName);
        Assert.Equal(auditBefore, await File.ReadAllTextAsync(paths.EventsLogPath));
        Assert.Empty(transport.Writes);
    }

    [Fact]
    public async Task Governed_authority_stop_is_not_masked_when_failure_audit_is_locked()
    {
        var stopped = new GovernedLoopEffectAuthorityStoppedException(
            "The governed provider effect was stopped.",
            GovernedLoopEffectAuthorityExecutionStatus.EvidenceRejected,
            GovernedLoopEffectAuthorityEvidenceStoreStatus.Unavailable,
            null);

        await AssertReviewCheckpointIsNotMaskedAsync(stopped);
    }

    [Fact]
    public async Task Tool_actuation_review_checkpoint_is_not_masked_when_failure_audit_is_locked()
    {
        var reviewRequired = new ToolActuationReviewRequiredException(
            ToolActuationAuthorityDisposition.Ambiguous,
            "The governed tool effect requires reconciliation.");

        await AssertReviewCheckpointIsNotMaskedAsync(reviewRequired);
    }

    private static async Task AssertReviewCheckpointIsNotMaskedAsync<TException>(TException expected)
        where TException : Exception
    {
        using var workspace = new TestWorkspace();
        var paths = new WorkspacePaths(workspace.RootPath);
        await PrepareAuditWorkspaceAsync(paths);
        var transport = new RecordingCodexAppServerTransport();
        await using var client = CreateCodexClient(workspace, transport);
        FileStream? auditLock = null;

        try
        {
            var exception = await Assert.ThrowsAsync<TException>(() => client.GenerateAsync(
                LlmInferenceRequest.FromUserText("hello"),
                responseChunkHandler: null,
                CancellationToken.None,
                (_, _) =>
                {
                    auditLock = new FileStream(paths.EventsLogPath, FileMode.Open, FileAccess.Read, FileShare.None);
                    return Task.FromException(expected);
                }));

            Assert.Same(expected, exception);
            Assert.Empty(transport.Writes);
        }
        finally
        {
            if (auditLock is not null)
            {
                await auditLock.DisposeAsync();
            }
        }

        var inferenceEvents = (await new AuditLog(paths).ReadTailAsync(20))
            .Where(auditEvent => auditEvent.Action.StartsWith("llm.inference.", StringComparison.Ordinal))
            .ToArray();
        var start = Assert.Single(inferenceEvents);
        Assert.Equal("llm.inference.start", start.Action);
        Assert.Equal("started", start.Outcome);
    }

    [Fact]
    public async Task GenerateAsync_records_failed_audit_event_when_provider_fails()
    {
        using var workspace = new TestWorkspace();
        var paths = new WorkspacePaths(workspace.RootPath);
        await PrepareAuditWorkspaceAsync(paths);
        var client = new LlmInferenceClient(new LlmInferenceClientOptions
        {
            Surface = LlmInferenceSurface.AzureAiFoundry,
            WorkingDirectory = workspace.RootPath
        });

        await Assert.ThrowsAsync<NotSupportedException>(() => client.GenerateAsync(LlmInferenceRequest.FromUserText("hello")));

        var events = await new AuditLog(paths).ReadTailAsync(2);
        Assert.Collection(
            events,
            auditEvent =>
            {
                Assert.Equal("llm.inference.start", auditEvent.Action);
                Assert.Equal("started", auditEvent.Outcome);
            },
            auditEvent =>
            {
                Assert.Equal("llm.inference.complete", auditEvent.Action);
                Assert.Equal("failed", auditEvent.Outcome);
            });
    }

    [Fact]
    public async Task GenerateAsync_audits_promoted_instruction_context_separately_from_messages()
    {
        using var workspace = new TestWorkspace();
        var paths = new WorkspacePaths(workspace.RootPath);
        await PrepareAuditWorkspaceAsync(paths);
        var governance = EmbodySenseDeveloperInstructions.Capture();
        var trustedInstructions = new[]
        {
            new EmbodySenseTrustedInstruction("role", "trusted role"),
            new EmbodySenseTrustedInstruction("identity", "durable identity")
        };
        var instructionContext = new LlmInferenceInstructionContext(governance, trustedInstructions);
        var request = new LlmInferenceRequest([LlmMessage.User("hello")], instructionContext: instructionContext);
        var client = new LlmInferenceClient(new LlmInferenceClientOptions
        {
            Surface = LlmInferenceSurface.AzureAiFoundry,
            WorkingDirectory = workspace.RootPath
        });

        await Assert.ThrowsAsync<NotSupportedException>(() => client.GenerateAsync(request));

        var start = Assert.Single(await new AuditLog(paths).ReadTailAsync(2), auditEvent => auditEvent.Action == "llm.inference.start");
        var composedInstructionCharacters = EmbodySenseDeveloperInstructions.Compose(governance, trustedInstructions).Length;
        Assert.Equal("1", start.Metadata["message_count"]?.ToString());
        Assert.Equal("5", start.Metadata["message_character_count"]?.ToString());
        Assert.Equal("2", start.Metadata["trusted_instruction_count"]?.ToString());
        Assert.Equal("28", start.Metadata["trusted_instruction_character_count"]?.ToString());
        Assert.Equal(composedInstructionCharacters.ToString(), start.Metadata["instruction_character_count"]?.ToString());
        Assert.Equal((5 + composedInstructionCharacters).ToString(), start.Metadata["input_character_count"]?.ToString());
    }

    private static LlmInferenceClient CreateCodexClient(TestWorkspace workspace, ICodexAppServerTransport transport)
    {
        return new LlmInferenceClient(new LlmInferenceClientOptions
        {
            Surface = LlmInferenceSurface.OpenAiCodex,
            Model = "gpt-test",
            WorkingDirectory = workspace.RootPath,
            CodexSandbox = "read-only"
        }, codexAppServerTransport: transport);
    }

    private static async Task PrepareAuditWorkspaceAsync(WorkspacePaths paths)
    {
        Directory.CreateDirectory(paths.AuditPath);
        await File.WriteAllTextAsync(paths.EventsLogPath, string.Empty);
    }
}
