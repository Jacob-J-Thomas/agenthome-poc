using System.Security.Cryptography;
using System.Text;
using EmbodySense.Core.Application.Loops.Execution.Custom;
using EmbodySense.Core.Common.Governance.Audit;
using EmbodySense.Core.Common.Governance.Tools;
using EmbodySense.Core.Common.Governance.Tools.Models;
using EmbodySense.Core.Common.Inference.Models;
using EmbodySense.Core.Common.Loops.Models.Custom;
using EmbodySense.Core.Common.Loops.Models.Custom.Execution;

namespace EmbodySense.Core.Application.Tests.Loops.Execution.Custom;

public sealed class CustomLoopContextResolverTests
{
    private static readonly DateTimeOffset Now = new(2026, 7, 16, 18, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Resolver_rejects_blank_node_instructions_and_tampered_admitted_context()
    {
        var blankDefinition = Definition(instruction: " ");
        var blankRun = Run(blankDefinition);
        var resolver = new CustomLoopContextResolver();

        Assert.Throws<InvalidOperationException>(() => resolver.ResolveInference(blankRun, blankDefinition.InferenceSteps[0]));

        var definition = Definition();
        var run = Run(definition);
        var tampered = run with { ContextSnapshot = run.ContextSnapshot with { ManifestHash = new string('0', CustomLoopLimits.Sha256HexCharacters) } };

        Assert.Throws<InvalidOperationException>(() => resolver.ResolveInference(tampered, definition.InferenceSteps[0]));
    }

    [Fact]
    public void Inference_assembles_the_complete_logical_request_in_contract_order_with_exact_manifest_hashes()
    {
        var policy = Policy(includeConversation: true);
        var definition = Definition(
            stepPolicy: CustomLoopNodeContextPolicy.Override(policy),
            triggerPolicy: new CustomLoopTriggerPolicy(CustomLoopTriggerPromptSource.Invocation, string.Empty, IncludeInvokingConversation: true),
            toolAssignments: [CustomLoopToolAssignment.List, CustomLoopToolAssignment.Search],
            instruction: "  Preserve this authored instruction exactly.  ");
        var checkpoint = new CustomLoopRunCheckpoint(
            2,
            1,
            1,
            false,
            [Retained("step-first", 2, "earlier retained output")],
            Retained("step-final", 1, "previous iteration result"),
            null,
            0,
            20);
        var run = Run(
            definition,
            checkpoint: checkpoint,
            triggerPrompt: "current invocation prompt",
            conversation: Conversation(),
            roleMessages:
            [
                new CustomLoopMessageSnapshot(LlmMessageRole.System, "role instructions"),
                new CustomLoopMessageSnapshot(LlmMessageRole.System, "bounded role state")
            ],
            conversationMessages:
            [
                new CustomLoopMessageSnapshot(LlmMessageRole.System, "untrusted prior system text"),
                new CustomLoopMessageSnapshot(LlmMessageRole.Assistant, "prior assistant response")
            ]);

        var assembly = new CustomLoopContextResolver().ResolveInference(run, definition.InferenceSteps[0]);

        var expectedSources = new[]
        {
            CustomLoopContextSource.HarnessGovernance,
            CustomLoopContextSource.RoleInstruction,
            CustomLoopContextSource.ContextualState,
            CustomLoopContextSource.RunMetadata,
            CustomLoopContextSource.NodeInstruction,
            CustomLoopContextSource.TriggerPrompt,
            CustomLoopContextSource.InvokingConversation,
            CustomLoopContextSource.InvokingConversation,
            CustomLoopContextSource.EarlierRetainedOutput,
            CustomLoopContextSource.PreviousIterationResult
        };

        var instructionContext = Assert.IsType<LlmInferenceInstructionContext>(assembly.Request.InstructionContext);
        Assert.True(EmbodySenseDeveloperInstructions.Matches(instructionContext.Governance, [ToolCommand.List, ToolCommand.Search]));
        Assert.Collection(
            instructionContext.TrustedInstructions,
            role =>
            {
                Assert.Equal("nearest-agents", role.SourceId);
                Assert.Equal("role instructions", role.Content);
            },
            node =>
            {
                Assert.Equal("step-main", node.SourceId);
                Assert.Equal("  Preserve this authored instruction exactly.  ", node.Content);
            });
        Assert.True(instructionContext.PreserveExactLogicalContext);
        Assert.All(assembly.Request.Messages, message => Assert.Equal(LlmMessageRole.User, message.Role));
        Assert.Equal("bounded role state", assembly.Request.Messages[0].Content);
        Assert.Contains(assembly.Request.Messages, message => message.Content.Contains("Loop: loop-context", StringComparison.Ordinal));
        Assert.Contains(assembly.Request.Messages, message => message.Content.Contains("current invocation prompt", StringComparison.Ordinal));
        Assert.Contains(assembly.Request.Messages, message => message.Content == "untrusted prior system text");
        Assert.Contains(assembly.Request.Messages, message => message.Content == "prior assistant response");
        Assert.Contains(assembly.Request.Messages, message => message.Content.Contains("earlier retained output", StringComparison.Ordinal));
        Assert.Contains(assembly.Request.Messages, message => message.Content.Contains("previous iteration result", StringComparison.Ordinal));
        Assert.Contains("trusted custom-loop node instruction", assembly.Request.Messages[^1].Content, StringComparison.Ordinal);
        Assert.Equal(policy.ContextOut, assembly.ResolvedOutputPolicy);
        Assert.Equal(expectedSources, assembly.Blocks.Where(block => block.Included).Select(block => block.Source));
        Assert.Equal(5, assembly.Blocks.Count(block => !block.Included && block.Source is CustomLoopContextSource.RoleInstruction or CustomLoopContextSource.AgentIdentity or CustomLoopContextSource.ContextualState));
        foreach (var block in assembly.Blocks.Where(block => block.Included))
        {
            Assert.Null(block.OmissionReason);
            Assert.Equal(Hash(block.Content), block.ContentHash);
            Assert.False(block.Truncated);
        }

        var governanceBlock = assembly.Blocks[0];
        Assert.Equal(EmbodySenseDeveloperInstructions.CurrentVersion, governanceBlock.SourceVersion);
        Assert.Equal(instructionContext.Governance.Content, governanceBlock.Content);

        Assert.DoesNotContain(assembly.Request.Messages, message => message.Content.Contains(definition.DisplayName, StringComparison.Ordinal));
        Assert.DoesNotContain(assembly.Request.Messages, message => message.Content.Contains(definition.Description, StringComparison.Ordinal));
        Assert.DoesNotContain(instructionContext.TrustedInstructions, message => message.Content.Contains(definition.Description, StringComparison.Ordinal));
    }

    [Fact]
    public void Inherited_policy_uses_the_admitted_definition_defaults_without_importing_unselected_conversation_history()
    {
        var definition = Definition(
            triggerPolicy: new CustomLoopTriggerPolicy(CustomLoopTriggerPromptSource.Invocation, string.Empty, IncludeInvokingConversation: true));
        var run = Run(
            definition,
            triggerPrompt: "prompt",
            conversation: Conversation(),
            roleMessages: [new CustomLoopMessageSnapshot(LlmMessageRole.System, "role")],
            conversationMessages: [new CustomLoopMessageSnapshot(LlmMessageRole.User, "conversation must stay out")]);

        var assembly = new CustomLoopContextResolver().ResolveInference(run, definition.InferenceSteps[0]);

        Assert.Equal(definition.ContextDefaults.Inference.ContextOut, assembly.ResolvedOutputPolicy);
        Assert.Contains(assembly.Request.InstructionContext!.TrustedInstructions, message => message.Content == "role");
        Assert.Contains(assembly.Request.Messages, message => message.Content.Contains("prompt", StringComparison.Ordinal));
        Assert.DoesNotContain(assembly.Request.Messages, message => message.Content.Contains("conversation must stay out", StringComparison.Ordinal));
        var omission = Assert.Single(assembly.Blocks, block => block.Source == CustomLoopContextSource.InvokingConversation);
        Assert.False(omission.Included);
        Assert.Equal("The resolved node context policy excluded invoking-conversation history.", omission.OmissionReason);
    }

    [Fact]
    public void Durable_agent_identity_enters_the_trusted_instruction_channel()
    {
        var definition = Definition();
        var run = Run(definition);
        var identityContent = "[EmbodySense durable agent identity source: .agent/SOUL.md]\nstable purpose";
        var manifest = run.ContextSnapshot.SourceManifest.ToArray();
        manifest[2] = new CustomLoopContextManifestSource(
            3,
            CustomLoopContextSource.AgentIdentity,
            "soul",
            "test/.agent/SOUL.md",
            CustomLoopContextProvenance.WorkspaceAgentIdentityFile,
            CustomLoopContextTrustClass.TrustedInstruction,
            LlmMessageRole.System,
            identityContent,
            Hash(identityContent),
            identityContent.Length,
            identityContent.Length,
            false,
            null,
            null,
            Now);
        run = run with
        {
            ContextSnapshot = CustomLoopContextSnapshotHash.Apply(run.ContextSnapshot with { SourceManifest = manifest })
        };

        var assembly = new CustomLoopContextResolver().ResolveInference(run, definition.InferenceSteps[0]);

        Assert.Contains(assembly.Request.InstructionContext!.TrustedInstructions, instruction => instruction.SourceId == "soul" && instruction.Content == identityContent);
        Assert.DoesNotContain(assembly.Request.Messages, message => message.Content == identityContent);
        Assert.Contains(assembly.Blocks, block => block.Source == CustomLoopContextSource.AgentIdentity && block.Included);
    }

    [Fact]
    public void Custom_policy_can_exclude_every_optional_source_and_preserves_its_output_disposition()
    {
        var outputPolicy = new CustomLoopContextOutputPolicy(RetainForLoopReasoning: false, PublishToInvokingConversation: true);
        var policy = Policy(
            includeRole: false,
            includeTrigger: false,
            includeConversation: false,
            includeEarlierOutputs: false,
            includePreviousIteration: false,
            output: outputPolicy);
        var definition = Definition(
            stepPolicy: CustomLoopNodeContextPolicy.Override(policy),
            triggerPolicy: new CustomLoopTriggerPolicy(CustomLoopTriggerPromptSource.Invocation, string.Empty, IncludeInvokingConversation: true));
        var run = Run(
            definition,
            checkpoint: new CustomLoopRunCheckpoint(1, 0, 0, false, [Retained("step-earlier", 1, "retained")], Retained("step-final", 0, "previous"), null, 0, 0),
            triggerPrompt: "trigger",
            conversation: Conversation(),
            roleMessages: [new CustomLoopMessageSnapshot(LlmMessageRole.System, "role")],
            conversationMessages: [new CustomLoopMessageSnapshot(LlmMessageRole.User, "conversation")]);

        var assembly = new CustomLoopContextResolver().ResolveInference(run, definition.InferenceSteps[0]);

        Assert.Equal(outputPolicy, assembly.ResolvedOutputPolicy);
        Assert.Equal(2, assembly.Request.Messages.Count);
        Assert.Equal(
            [CustomLoopContextSource.HarnessGovernance, CustomLoopContextSource.RunMetadata, CustomLoopContextSource.NodeInstruction],
            assembly.Blocks.Where(block => block.Included).Select(block => block.Source));
        Assert.Equal(7, assembly.Blocks.Count(block => !block.Included && block.Source is CustomLoopContextSource.RoleInstruction or CustomLoopContextSource.AgentIdentity or CustomLoopContextSource.ContextualState));
        Assert.Contains(assembly.Blocks, block => !block.Included && block.Source == CustomLoopContextSource.TriggerPrompt);
        Assert.Contains(assembly.Blocks, block => !block.Included && block.Source == CustomLoopContextSource.InvokingConversation);
        Assert.Contains(assembly.Blocks, block => !block.Included && block.Source == CustomLoopContextSource.EarlierRetainedOutput);
        Assert.Contains(assembly.Blocks, block => !block.Included && block.Source == CustomLoopContextSource.PreviousIterationResult);
        Assert.All(
            assembly.Blocks.Where(block => !block.Included),
            block =>
            {
                Assert.NotNull(block.OmissionReason);
                Assert.Equal(string.Empty, block.Content);
                Assert.Equal(Hash(string.Empty), block.ContentHash);
                Assert.Equal(0, block.CharacterCount);
                Assert.False(block.Truncated);
            });
        Assert.Contains(assembly.Request.Messages, message => message.Content.EndsWith("Tools: none", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(false, true, true, false, "Trigger did not admit invoking-conversation history.")]
    [InlineData(true, false, true, false, "The resolved node context policy excluded invoking-conversation history.")]
    [InlineData(true, true, false, false, "No invoking conversation was bound at admission.")]
    [InlineData(true, true, true, true, null)]
    public void Conversation_history_requires_trigger_admission_node_selection_and_an_admitted_binding(
        bool triggerAdmits,
        bool nodeSelects,
        bool hasBinding,
        bool expectedIncluded,
        string? expectedOmissionReason)
    {
        var policy = Policy(includeConversation: nodeSelects);
        var definition = Definition(
            stepPolicy: CustomLoopNodeContextPolicy.Override(policy),
            triggerPolicy: new CustomLoopTriggerPolicy(CustomLoopTriggerPromptSource.Invocation, string.Empty, triggerAdmits));
        var run = Run(
            definition,
            conversation: hasBinding ? Conversation() : null,
            conversationMessages: [new CustomLoopMessageSnapshot(LlmMessageRole.User, "bounded prior conversation")]);

        var assembly = new CustomLoopContextResolver().ResolveInference(run, definition.InferenceSteps[0]);
        var block = Assert.Single(assembly.Blocks, item => item.Source == CustomLoopContextSource.InvokingConversation);

        Assert.Equal(expectedIncluded, block.Included);
        Assert.Equal(expectedOmissionReason, block.OmissionReason);
        Assert.Equal(expectedIncluded, assembly.Request.Messages.Any(message => message.Content.Contains("bounded prior conversation", StringComparison.Ordinal)));
    }

    [Fact]
    public void Only_retained_outputs_enter_later_context_while_evidence_only_output_remains_in_the_trace()
    {
        var policy = Policy(includeEarlierOutputs: true, includePreviousIteration: true);
        var definition = Definition(stepPolicy: CustomLoopNodeContextPolicy.Override(policy));
        var evidenceOnly = "evidence-only output must not re-enter model context";
        var retained = Retained("step-retained", 1, "retained output enters context");
        var checkpoint = new CustomLoopRunCheckpoint(1, 1, 0, false, [retained], null, Retained("step-evidence", 1, evidenceOnly), 0, 2);
        var evidenceEvent = new CustomLoopRunEvent(
            1,
            "event-evidence",
            Now,
            CustomLoopRunEventKind.NodeAttemptCompleted,
            1,
            "step-evidence",
            1,
            "Evidence-only output was observed.",
            [],
            evidenceOnly,
            evidenceOnly.Length,
            false,
            false,
            false,
            null,
            "provider",
            "model",
            "response-1",
            null);
        var run = Run(definition, checkpoint: checkpoint, events: [evidenceEvent]);

        var assembly = new CustomLoopContextResolver().ResolveInference(run, definition.InferenceSteps[0]);

        Assert.Contains(assembly.Request.Messages, message => message.Content.Contains(retained.Content, StringComparison.Ordinal));
        Assert.DoesNotContain(assembly.Request.Messages, message => message.Content.Contains(evidenceOnly, StringComparison.Ordinal));
        Assert.Equal(evidenceOnly, Assert.Single(run.Events).CanonicalOutput);
        var previousOmission = Assert.Single(assembly.Blocks, block => block.Source == CustomLoopContextSource.PreviousIterationResult);
        Assert.False(previousOmission.Included);
        Assert.Equal("No previous iteration result was retained at the repeat boundary.", previousOmission.OmissionReason);
    }

    [Fact]
    public void Exit_request_is_explicitly_toolless_and_preserves_the_authored_decision_instruction()
    {
        var outputPolicy = new CustomLoopContextOutputPolicy(RetainForLoopReasoning: false, PublishToInvokingConversation: true);
        var exitPolicy = new CustomLoopExitPolicy(
            2,
            "  Decide whether another iteration is useful.  ",
            CustomLoopNodeContextPolicy.Override(Policy(
                includeRole: false,
                includeTrigger: false,
                includeConversation: false,
                includeEarlierOutputs: false,
                includePreviousIteration: false,
                output: outputPolicy)));
        var definition = Definition(
            toolAssignments: [CustomLoopToolAssignment.List, CustomLoopToolAssignment.Read],
            exitPolicy: exitPolicy);
        var run = Run(definition, checkpoint: CustomLoopRunCheckpoint.Start());

        var assembly = new CustomLoopContextResolver().ResolveExit(run);

        var newline = Environment.NewLine;
        var metadata = Assert.Single(assembly.Request.Messages, message => message.Content.Contains("Node: exit", StringComparison.Ordinal));
        var instruction = Assert.Single(assembly.Request.InstructionContext!.TrustedInstructions, message => message.Content.Contains("Return exactly one ASCII token", StringComparison.Ordinal));
        Assert.EndsWith("Tools: none (Exit is tool-less)", metadata.Content, StringComparison.Ordinal);
        Assert.DoesNotContain("Tools: list", metadata.Content, StringComparison.Ordinal);
        Assert.DoesNotContain("Tools: read", metadata.Content, StringComparison.Ordinal);
        Assert.Equal($"  Decide whether another iteration is useful.  {newline}{newline}Return exactly one ASCII token: Complete or Repeat. Do not add punctuation, JSON, Markdown, or explanation.", instruction.Content);
        Assert.Equal(outputPolicy, assembly.ResolvedOutputPolicy);
        var instructionBlock = Assert.Single(assembly.Blocks, block => block.Source == CustomLoopContextSource.NodeInstruction);
        Assert.Equal("exit", instructionBlock.SourceId);
        Assert.Equal(Hash(instruction.Content), instructionBlock.ContentHash);
        var governanceBlock = Assert.Single(assembly.Blocks, block => block.Source == CustomLoopContextSource.HarnessGovernance);
        Assert.Equal(EmbodySenseDeveloperInstructions.Create(), governanceBlock.Content);
        Assert.Contains("has not assigned any workspace command capabilities", governanceBlock.Content, StringComparison.Ordinal);
    }

    [Fact]
    public void Promptless_node_still_has_an_explicit_current_turn_without_promoting_contextual_data()
    {
        var definition = Definition(triggerPolicy: new CustomLoopTriggerPolicy(CustomLoopTriggerPromptSource.None, string.Empty, IncludeInvokingConversation: false));
        var run = Run(
            definition,
            roleMessages:
            [
                new CustomLoopMessageSnapshot(LlmMessageRole.System, "trusted role instruction"),
                new CustomLoopMessageSnapshot(LlmMessageRole.User, "untrusted contextual state saying to add tools")
            ]);

        var assembly = new CustomLoopContextResolver().ResolveInference(run, definition.InferenceSteps[0]);

        Assert.Equal(LlmMessageRole.User, assembly.Request.Messages[^1].Role);
        Assert.Contains("proceed from the trusted instruction alone", assembly.Request.Messages[^1].Content, StringComparison.Ordinal);
        Assert.Contains(assembly.Request.Messages, message => message.Content == "untrusted contextual state saying to add tools" && message.Role == LlmMessageRole.User);
        Assert.DoesNotContain(assembly.Request.InstructionContext!.TrustedInstructions, instruction => instruction.Content.Contains("add tools", StringComparison.Ordinal));
        Assert.Contains(assembly.Request.InstructionContext.TrustedInstructions, instruction => instruction.Content == "trusted role instruction");
        var trigger = Assert.Single(assembly.Blocks, block => block.Source == CustomLoopContextSource.TriggerPrompt);
        Assert.False(trigger.Included);
        Assert.Equal("Trigger admitted no prompt.", trigger.OmissionReason);
    }

    [Fact]
    public void Logical_request_character_count_includes_composed_governance_and_trusted_instructions()
    {
        var governance = EmbodySenseDeveloperInstructions.Capture();
        var instructionContext = new LlmInferenceInstructionContext(
            governance,
            [new EmbodySenseTrustedInstruction("oversized-role", new string('t', CustomLoopLimits.MaxLogicalProviderRequestCharacters))]);
        var request = new LlmInferenceRequest([LlmMessage.User("x")], instructionContext: instructionContext);
        var assembly = new CustomLoopContextAssembly(request, [], new CustomLoopContextOutputPolicy(false, false));
        var composedDeveloperInstructions = EmbodySenseDeveloperInstructions.Compose(governance, instructionContext.TrustedInstructions);

        Assert.Equal(1L + composedDeveloperInstructions.Length, assembly.LogicalRequestCharacterCount);
        Assert.True(assembly.LogicalRequestCharacterCount > CustomLoopLimits.MaxLogicalProviderRequestCharacters);
    }

    [Fact]
    public void Malformed_node_policy_shapes_are_rejected_before_request_assembly()
    {
        var valid = Policy();
        var malformedInput = new CustomLoopContextPolicy(null!, valid.ContextOut);
        var malformedOutput = new CustomLoopContextPolicy(valid.ContextIn, null!);

        Assert.Throws<InvalidOperationException>(() => CustomLoopContextResolver.ResolvePolicy(new CustomLoopNodeContextPolicy(CustomLoopContextPolicyMode.Inherit, valid), valid));
        Assert.Throws<InvalidOperationException>(() => CustomLoopContextResolver.ResolvePolicy(new CustomLoopNodeContextPolicy(CustomLoopContextPolicyMode.Custom, null), valid));
        Assert.Throws<InvalidOperationException>(() => CustomLoopContextResolver.ResolvePolicy(new CustomLoopNodeContextPolicy(CustomLoopContextPolicyMode.Unknown, null), valid));
        Assert.Throws<InvalidOperationException>(() => CustomLoopContextResolver.ResolvePolicy(CustomLoopNodeContextPolicy.Inherit(), malformedInput));
        Assert.Throws<InvalidOperationException>(() => CustomLoopContextResolver.ResolvePolicy(CustomLoopNodeContextPolicy.Override(malformedOutput), valid));
    }

    [Fact]
    public void Inference_attempt_contract_carries_the_complete_run_boundary_and_provider_correlation()
    {
        var inferenceRequest = LlmInferenceRequest.FromUserText("logical request");
        var attempt = new CustomLoopInferenceAttemptRequest(
            "run-context",
            "loop-context",
            "role-context",
            2,
            Hash("definition"),
            2,
            "step-main",
            1,
            "attempt-1",
            IsExit: false,
            AllowTools: true,
            new CustomLoopModelSnapshot("provider", "model"),
            [CustomLoopToolAssignment.Read],
            0,
            inferenceRequest);
        var result = new CustomLoopInferenceAttemptResult("canonical output", "provider", "model", "response-1");

        Assert.Equal("run-context", attempt.RunId);
        Assert.Equal("loop-context", attempt.LoopId);
        Assert.Equal("role-context", attempt.RoleId);
        Assert.Equal(2, attempt.Iteration);
        Assert.Equal("step-main", attempt.StepId);
        Assert.Equal(1, attempt.Attempt);
        Assert.True(attempt.AllowTools);
        Assert.Same(inferenceRequest, attempt.InferenceRequest);
        Assert.Equal("canonical output", result.OutputText);
        Assert.Equal("provider", result.Provider);
        Assert.Equal("model", result.Model);
        Assert.Equal("response-1", result.ProviderResponseId);
    }

    private static CustomLoopDefinition Definition(
        CustomLoopNodeContextPolicy? stepPolicy = null,
        CustomLoopTriggerPolicy? triggerPolicy = null,
        CustomLoopToolAssignment[]? toolAssignments = null,
        string instruction = "Perform the admitted step.",
        CustomLoopExitPolicy? exitPolicy = null)
    {
        var seed = CustomLoopDefinition.CreateSeed("loop-context", "role-context", "step-main", "op-create", Now);
        var definition = seed with
        {
            DisplayName = "Display name is not model context",
            Description = "Additional fixed context must never enter from definition metadata",
            TriggerPolicy = triggerPolicy ?? seed.TriggerPolicy,
            InferenceSteps = [seed.InferenceSteps[0] with { Instruction = instruction, ContextPolicy = stepPolicy ?? CustomLoopNodeContextPolicy.Inherit() }],
            ToolAssignments = toolAssignments ?? [],
            ExitPolicy = exitPolicy ?? seed.ExitPolicy
        };
        return CustomLoopDefinitionContentHash.Apply(definition);
    }

    private static CustomLoopRunRecord Run(
        CustomLoopDefinition definition,
        CustomLoopRunCheckpoint? checkpoint = null,
        string triggerPrompt = "",
        CustomLoopConversationReference? conversation = null,
        CustomLoopMessageSnapshot[]? roleMessages = null,
        CustomLoopMessageSnapshot[]? conversationMessages = null,
        CustomLoopRunEvent[]? events = null)
    {
        return new CustomLoopRunRecord(
            CustomLoopRunRecord.CurrentSchemaVersion,
            "run-context",
            definition.Id,
            1,
            CustomLoopRunStatus.Running,
            Now,
            Now,
            null,
            "web",
            new CustomLoopModelSnapshot("provider", "model"),
            "op-admit",
            AuditSchema.Actors.Web,
            Hash("admission"),
            definition,
            triggerPrompt,
            conversation,
            CustomLoopContextSnapshotHash.Apply(new CustomLoopContextSnapshot(
                CustomLoopContextSnapshot.CurrentSchemaVersion,
                Now,
                CreateManifest(roleMessages ?? [], conversationMessages ?? []),
                string.Empty)),
            CustomLoopExecutionClock.NotStarted(),
            checkpoint ?? CustomLoopRunCheckpoint.Start(),
            events ?? [],
            null,
            null,
            null);
    }

    private static CustomLoopConversationReference Conversation()
    {
        return new CustomLoopConversationReference("conversation-1", "version-1", Now);
    }

    private static CustomLoopContextManifestSource[] CreateManifest(
        IReadOnlyList<CustomLoopMessageSnapshot> roleMessages,
        IReadOnlyList<CustomLoopMessageSnapshot> conversationMessages)
    {
        var roleInstruction = roleMessages.Count > 0 ? IncludedSource(1, CustomLoopContextSource.RoleInstruction, "nearest-agents", CustomLoopContextProvenance.WorkspaceRoleFile, CustomLoopContextTrustClass.TrustedInstruction, LlmMessageRole.System, roleMessages[0].Content) : OmittedSource(1, CustomLoopContextSource.RoleInstruction, "nearest-agents", CustomLoopContextProvenance.WorkspaceRoleFile, CustomLoopContextTrustClass.TrustedInstruction, LlmMessageRole.System);
        var contextualState = roleMessages.Count > 1 ? IncludedSource(5, CustomLoopContextSource.ContextualState, "context", CustomLoopContextProvenance.WorkspaceContextFile, CustomLoopContextTrustClass.UntrustedData, LlmMessageRole.User, roleMessages[1].Content) : OmittedSource(5, CustomLoopContextSource.ContextualState, "context", CustomLoopContextProvenance.WorkspaceContextFile, CustomLoopContextTrustClass.UntrustedData, LlmMessageRole.User);
        var manifest = new List<CustomLoopContextManifestSource>
        {
            roleInstruction,
            OmittedSource(2, CustomLoopContextSource.RoleInstruction, "role", CustomLoopContextProvenance.WorkspaceRoleFile, CustomLoopContextTrustClass.TrustedInstruction, LlmMessageRole.System),
            OmittedSource(3, CustomLoopContextSource.AgentIdentity, "soul", CustomLoopContextProvenance.WorkspaceAgentIdentityFile, CustomLoopContextTrustClass.TrustedInstruction, LlmMessageRole.System),
            OmittedSource(4, CustomLoopContextSource.AgentIdentity, "personality", CustomLoopContextProvenance.WorkspaceAgentIdentityFile, CustomLoopContextTrustClass.TrustedInstruction, LlmMessageRole.System),
            contextualState,
            OmittedSource(6, CustomLoopContextSource.ContextualState, "memory", CustomLoopContextProvenance.WorkspaceContextFile, CustomLoopContextTrustClass.UntrustedData, LlmMessageRole.User),
            OmittedSource(7, CustomLoopContextSource.ContextualState, "models", CustomLoopContextProvenance.WorkspaceContextFile, CustomLoopContextTrustClass.UntrustedData, LlmMessageRole.User)
        };
        for (var index = 0; index < conversationMessages.Count; index++)
        {
            manifest.Add(IncludedSource(8 + index, CustomLoopContextSource.InvokingConversation, $"invoking-conversation-{index + 1}", CustomLoopContextProvenance.LogicalConversation, CustomLoopContextTrustClass.UntrustedData, LlmMessageRole.User, conversationMessages[index].Content));
        }

        return manifest.ToArray();
    }

    private static CustomLoopContextManifestSource IncludedSource(
        int order,
        CustomLoopContextSource source,
        string sourceId,
        CustomLoopContextProvenance provenance,
        CustomLoopContextTrustClass trustClass,
        LlmMessageRole role,
        string content)
    {
        return new CustomLoopContextManifestSource(order, source, sourceId, $"test/{sourceId}", provenance, trustClass, role, content, Hash(content), content.Length, content.Length, false, null, null, Now);
    }

    private static CustomLoopContextManifestSource OmittedSource(
        int order,
        CustomLoopContextSource source,
        string sourceId,
        CustomLoopContextProvenance provenance,
        CustomLoopContextTrustClass trustClass,
        LlmMessageRole role)
    {
        return new CustomLoopContextManifestSource(order, source, sourceId, $"test/{sourceId}", provenance, trustClass, role, string.Empty, Hash(string.Empty), 0, 0, false, null, "Source absent in test fixture.", Now);
    }

    private static CustomLoopRetainedOutput Retained(string stepId, int iteration, string content)
    {
        return new CustomLoopRetainedOutput(stepId, iteration, content, Hash(content));
    }

    private static CustomLoopContextPolicy Policy(
        bool includeRole = true,
        bool includeTrigger = true,
        bool includeConversation = false,
        bool includeEarlierOutputs = true,
        bool includePreviousIteration = true,
        CustomLoopContextOutputPolicy? output = null)
    {
        return new CustomLoopContextPolicy(
            new CustomLoopContextInputPolicy(includeRole, includeTrigger, includeConversation, includeEarlierOutputs, includePreviousIteration),
            output ?? new CustomLoopContextOutputPolicy(RetainForLoopReasoning: true, PublishToInvokingConversation: false));
    }

    private static string Hash(string content)
    {
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(content))).ToLowerInvariant();
    }
}
