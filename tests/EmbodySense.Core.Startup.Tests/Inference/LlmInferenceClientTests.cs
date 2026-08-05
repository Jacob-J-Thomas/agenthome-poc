using EmbodySense.Core.Common.Inference;
using EmbodySense.Core.Application.Governance.Audit;
using EmbodySense.Core.Common.Governance.Audit;
using EmbodySense.Core.Common.Governance.Tools;
using EmbodySense.Core.Common.Governance.Tools.Models;
using EmbodySense.Core.Common.Inference.Models;
using EmbodySense.Core.Persistence.Audit;
using EmbodySense.Core.Startup.Workspace;
using EmbodySense.Core.Common.Workspace;
using EmbodySense.Core.Startup.Inference;
using EmbodySense.Tests.Support;

namespace EmbodySense.Core.Startup.Tests.Inference;

public sealed class LlmInferenceClientTests
{
    [Fact]
    public async Task GenerateAsync_records_failed_audit_event_when_provider_fails()
    {
        using var workspace = new TestWorkspace();
        var paths = new WorkspacePaths(workspace.RootPath);
        await WorkspaceInitializer.ForFileCapabilityTrustRoot(workspace.ServerStatePath).InitializeAsync(workspace.RootPath);
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
        await WorkspaceInitializer.ForFileCapabilityTrustRoot(workspace.ServerStatePath).InitializeAsync(workspace.RootPath);
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
}
