using EmbodySense.Core.Common.Inference.Models;
using EmbodySense.Core.Common.Loops.Custom;
using EmbodySense.Core.Common.Loops.Custom.Execution;
using EmbodySense.Core.Common.Loops.Execution;
using EmbodySense.Core.Common.Loops.Models.Custom.Execution;
using EmbodySense.Core.Common.Loops.Models.Custom.Graph;
using EmbodySense.Core.Common.Loops.Sequential;
using EmbodySense.Core.Common.Loops.Sequential.Models;

namespace EmbodySense.Core.Common.Tests.Loops.Sequential;

public sealed class GovernedLoopSequentialContractTests
{
    private static readonly DateTimeOffset _capturedAtUtc = new(2026, 8, 10, 21, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Invocation_snapshot_hash_binds_every_exact_payload_coordinate()
    {
        var snapshot = Snapshot();

        Assert.True(GovernedLoopSequentialContractValidator.Validate(snapshot).IsValid);
        Assert.True(GovernedLoopSequentialContractHash.Matches(snapshot));
        Assert.False(GovernedLoopSequentialContractHash.Matches(snapshot with { TriggerPrompt = "Different prompt." }));
        Assert.False(GovernedLoopSequentialContractHash.Matches(CopySnapshot(snapshot, model: new CustomLoopModelSnapshot("provider", "other-model"))));
        Assert.False(GovernedLoopSequentialContractHash.Matches(CopySnapshot(snapshot, conversation: snapshot.InvokingConversation! with { CapturedVersion = "version-2" })));
        Assert.False(GovernedLoopSequentialContractHash.Matches(snapshot with { ContextCapturedAtUtc = snapshot.ContextCapturedAtUtc.AddSeconds(1) }));
    }

    [Fact]
    public void Invocation_snapshot_defensively_copies_nested_values_and_manifest_storage()
    {
        var context = CustomLoopContextSnapshot.CreateEmpty(_capturedAtUtc);
        var mutableManifest = context.SourceManifest.ToArray();
        var model = new CustomLoopModelSnapshot("provider", "model");
        var conversation = new CustomLoopConversationReference("conversation-1", "version-1", _capturedAtUtc.AddMinutes(-1));
        var snapshot = GovernedLoopSequentialContractHash.Apply(new GovernedLoopSequentialInvocationSnapshot(
            1,
            "Answer the admitted request.",
            model,
            conversation,
            _capturedAtUtc,
            mutableManifest,
            string.Empty));

        mutableManifest[0] = mutableManifest[0] with { SourceId = "substituted" };

        Assert.NotSame(model, snapshot.ModelSnapshot);
        Assert.NotSame(conversation, snapshot.InvokingConversation);
        Assert.Equal("nearest-agents", snapshot.ContextManifest[0].SourceId);
        var exposed = Assert.IsAssignableFrom<IList<CustomLoopContextManifestSource>>(snapshot.ContextManifest);
        Assert.Throws<NotSupportedException>(() => exposed[0] = exposed[0] with { SourceId = "mutated" });
        Assert.True(GovernedLoopSequentialContractValidator.Validate(snapshot).IsValid);
    }

    [Fact]
    public void Invocation_snapshot_rejects_oversized_malformed_and_internally_substituted_values()
    {
        var valid = Snapshot();
        var oversizedPrompt = valid with
        {
            TriggerPrompt = new string('x', GovernedLoopSequentialContractLimits.MaxTriggerPromptCharacters + 1),
        };
        var malformedPrompt = valid with { TriggerPrompt = "bad\ud800" };
        var substitutedSource = valid.ContextManifest.ToArray();
        substitutedSource[0] = substitutedSource[0] with { Content = "forged" };
        var substitutedContext = CopySnapshot(valid, contextManifest: substitutedSource);
        var overLimitContext = CopySnapshot(
            valid,
            contextManifest: Enumerable.Range(1, GovernedLoopSequentialContractLimits.MaxContextSources + 1)
                .Select(index => valid.ContextManifest[0] with { Order = index, SourceId = $"source-{index:D3}" })
                .ToArray());

        Assert.Contains(GovernedLoopSequentialContractValidator.Validate(oversizedPrompt).Errors, error => error.Code == GovernedLoopSequentialValidationErrorCode.InvalidText && error.Path == "$.triggerPrompt");
        Assert.Contains(GovernedLoopSequentialContractValidator.Validate(malformedPrompt).Errors, error => error.Code == GovernedLoopSequentialValidationErrorCode.InvalidText && error.Path == "$.triggerPrompt");
        Assert.Contains(GovernedLoopSequentialContractValidator.Validate(substitutedContext).Errors, error => error.Code == GovernedLoopSequentialValidationErrorCode.HashMismatch && error.Path == "$.contextManifest[0].contentHash");
        Assert.Contains(GovernedLoopSequentialContractValidator.Validate(overLimitContext).Errors, error => error.Code == GovernedLoopSequentialValidationErrorCode.CollectionTooLarge && error.Path == "$.contextManifest");
    }

    [Fact]
    public void Adapter_binding_hash_binds_workspace_run_receipt_request_invocation_and_graph_coordinates()
    {
        var binding = AdapterBinding();

        Assert.True(GovernedLoopSequentialContractValidator.Validate(binding).IsValid);
        Assert.True(GovernedLoopSequentialContractHash.Matches(binding));
        Assert.NotSame(AdapterBinding().ExecutionBinding, binding.ExecutionBinding);
        Assert.False(GovernedLoopSequentialContractHash.Matches(binding with { WorkspaceId = WorkspaceId('b') }));
        Assert.False(GovernedLoopSequentialContractHash.Matches(CopyBinding(binding, execution: GovernedLoopExecutionBinding.Create(1, "run-2", binding.ExecutionBinding.Revision, 1))));
        Assert.False(GovernedLoopSequentialContractHash.Matches(binding with { AdmissionReceiptHash = Hash('b') }));
        Assert.False(GovernedLoopSequentialContractHash.Matches(binding with { AdmissionRequestHash = Hash('c') }));
        Assert.False(GovernedLoopSequentialContractHash.Matches(binding with { InvocationPayloadHash = Hash('d') }));
        Assert.False(GovernedLoopSequentialContractHash.Matches(binding with { GraphArtifactHash = Hash('e') }));
        Assert.False(GovernedLoopSequentialContractHash.Matches(binding with { GraphLayoutHash = Hash('f') }));
    }

    [Fact]
    public void Adapter_binding_validation_rejects_noncanonical_schema_identity_and_hash_shapes()
    {
        var binding = AdapterBinding() with
        {
            SchemaVersion = 2,
            WorkspaceId = "workspace",
            AdmissionOperationId = "not valid!",
            AdmissionReceiptHash = "ABC",
        };

        var result = GovernedLoopSequentialContractValidator.Validate(binding);

        Assert.Contains(result.Errors, error => error.Code == GovernedLoopSequentialValidationErrorCode.UnsupportedSchemaVersion && error.Path == "$.schemaVersion");
        Assert.Contains(result.Errors, error => error.Code == GovernedLoopSequentialValidationErrorCode.InvalidIdentity && error.Path == "$.workspaceId");
        Assert.Contains(result.Errors, error => error.Code == GovernedLoopSequentialValidationErrorCode.InvalidIdentity && error.Path == "$.admissionOperationId");
        Assert.Contains(result.Errors, error => error.Code == GovernedLoopSequentialValidationErrorCode.InvalidHash && error.Path == "$.admissionReceiptHash");
    }

    [Fact]
    public void Workspace_manifest_preserves_the_exact_seven_source_trust_classifications()
    {
        var valid = Snapshot();
        var substitutions = new Func<CustomLoopContextManifestSource, CustomLoopContextManifestSource>[]
        {
            source => source with { SourceId = "other" },
            source => source with { SourcePath = "workspace/OTHER.md" },
            source => source with { SourceType = CustomLoopContextSource.ContextualState },
            source => source with { Provenance = CustomLoopContextProvenance.LogicalConversation },
            source => source with { TrustClass = CustomLoopContextTrustClass.UntrustedData },
            source => source with { Role = LlmMessageRole.User },
        };

        foreach (var substitute in substitutions)
        {
            var manifest = valid.ContextManifest.ToArray();
            manifest[0] = substitute(manifest[0]);
            var candidate = CopySnapshot(valid, contextManifest: manifest);

            Assert.Contains(GovernedLoopSequentialContractValidator.Validate(candidate).Errors, error => error.Code == GovernedLoopSequentialValidationErrorCode.InvalidComposition && error.Path == "$.contextManifest[0]");
            Assert.Throws<ArgumentException>(() => GovernedLoopSequentialContractHash.Compute(candidate));
        }
    }

    [Fact]
    public void Context_tail_is_only_untrusted_logical_conversation_data_and_requires_a_conversation_reference()
    {
        var valid = Snapshot();
        var tail = new CustomLoopContextManifestSource(
            8,
            CustomLoopContextSource.InvokingConversation,
            "conversation-message-1",
            "conversation/conversation-1/version-1/message-1",
            CustomLoopContextProvenance.LogicalConversation,
            CustomLoopContextTrustClass.UntrustedData,
            LlmMessageRole.User,
            "Hello.",
            CustomLoopTraceContentHash.Compute("Hello."),
            6,
            6,
            false,
            null,
            null,
            _capturedAtUtc);
        var manifest = valid.ContextManifest.Append(tail).ToArray();
        var accepted = GovernedLoopSequentialContractHash.Apply(CopySnapshot(valid, contextManifest: manifest) with { ContentHash = string.Empty });
        var elevated = manifest.ToArray();
        elevated[^1] = elevated[^1] with { TrustClass = CustomLoopContextTrustClass.TrustedInstruction, Role = LlmMessageRole.System };
        var elevatedCandidate = CopySnapshot(valid, contextManifest: elevated);
        var missingReference = new GovernedLoopSequentialInvocationSnapshot(
            valid.SchemaVersion,
            valid.TriggerPrompt,
            valid.ModelSnapshot,
            null,
            valid.ContextCapturedAtUtc,
            manifest,
            valid.ContentHash);

        Assert.True(GovernedLoopSequentialContractValidator.Validate(accepted).IsValid);
        Assert.Contains(GovernedLoopSequentialContractValidator.Validate(elevatedCandidate).Errors, error => error.Code == GovernedLoopSequentialValidationErrorCode.InvalidComposition && error.Path == "$.contextManifest[7]");
        Assert.Contains(GovernedLoopSequentialContractValidator.Validate(missingReference).Errors, error => error.Code == GovernedLoopSequentialValidationErrorCode.InvalidComposition && error.Path == "$.invokingConversation");
    }

    [Fact]
    public void Workspace_context_source_cannot_exceed_the_ordered_runtime_instruction_bound()
    {
        var valid = Snapshot();
        var content = new string('x', CustomLoopLimits.MaxInstructionCharacters + 1);
        var manifest = valid.ContextManifest.ToArray();
        manifest[0] = manifest[0] with
        {
            Content = content,
            ContentHash = CustomLoopTraceContentHash.Compute(content),
            OriginalCharacterCount = content.Length,
            UsedCharacterCount = content.Length,
            OmissionReason = null,
        };
        var candidate = CopySnapshot(valid, contextManifest: manifest);

        Assert.Contains(GovernedLoopSequentialContractValidator.Validate(candidate).Errors, error => error.Code == GovernedLoopSequentialValidationErrorCode.CollectionTooLarge && error.Path == "$.contextManifest[0].usedCharacterCount");
    }

    [Theory]
    [InlineData("bad/id")]
    [InlineData("-conversation")]
    [InlineData("conversation-")]
    [InlineData("con")]
    public void Conversation_identity_matches_the_existing_artifact_identifier_contract(string conversationId)
    {
        var valid = Snapshot();
        var candidate = CopySnapshot(valid, conversation: valid.InvokingConversation! with { ConversationId = conversationId });

        Assert.Contains(GovernedLoopSequentialContractValidator.Validate(candidate).Errors, error => error.Code == GovernedLoopSequentialValidationErrorCode.InvalidIdentity && error.Path == "$.invokingConversation.conversationId");
    }

    [Fact]
    public void Admission_operation_token_matches_upstream_dot_and_boundary_rules()
    {
        var valid = AdapterBinding();
        var dotted = GovernedLoopSequentialContractHash.Apply(CopyBinding(valid, valid.ExecutionBinding, "admit.loop_1-step"));

        Assert.True(GovernedLoopSequentialContractValidator.Validate(dotted).IsValid);
        foreach (var invalid in new[] { "-admit", "_admit", ".admit", "admit-", "admit_", "admit." })
        {
            var candidate = CopyBinding(valid, valid.ExecutionBinding, invalid);
            Assert.Contains(GovernedLoopSequentialContractValidator.Validate(candidate).Errors, error => error.Code == GovernedLoopSequentialValidationErrorCode.InvalidIdentity && error.Path == "$.admissionOperationId");
        }
    }

    [Fact]
    public void Public_validation_reports_null_nested_required_hash_and_timestamp_failures_without_throwing()
    {
        var validSnapshot = Snapshot();
        var validBinding = AdapterBinding();
        var missingSnapshotValues = new GovernedLoopSequentialInvocationSnapshot(1, "Prompt.", null!, null, default, null!, Hash('f'));
        var missingBindingValues = new GovernedLoopSequentialAdapterBinding(1, validBinding.WorkspaceId, null!, "admit-1", Hash('1'), Hash('2'), Hash('3'), Hash('4'), Hash('5'), Hash('6'));
        var futureConversation = CopySnapshot(validSnapshot, conversation: validSnapshot.InvokingConversation! with { CapturedAtUtc = validSnapshot.ContextCapturedAtUtc.AddSeconds(1) });

        Assert.Contains(GovernedLoopSequentialContractValidator.Validate((GovernedLoopSequentialInvocationSnapshot?)null).Errors, error => error.Code == GovernedLoopSequentialValidationErrorCode.Required && error.Path == "$");
        Assert.Contains(GovernedLoopSequentialContractValidator.Validate((GovernedLoopSequentialAdapterBinding?)null).Errors, error => error.Code == GovernedLoopSequentialValidationErrorCode.Required && error.Path == "$");
        Assert.Contains(GovernedLoopSequentialContractValidator.Validate(missingSnapshotValues).Errors, error => error.Code == GovernedLoopSequentialValidationErrorCode.Required && error.Path == "$.modelSnapshot");
        Assert.Contains(GovernedLoopSequentialContractValidator.Validate(missingSnapshotValues).Errors, error => error.Code == GovernedLoopSequentialValidationErrorCode.Required && error.Path == "$.contextManifest");
        Assert.Contains(GovernedLoopSequentialContractValidator.Validate(missingSnapshotValues).Errors, error => error.Code == GovernedLoopSequentialValidationErrorCode.InvalidTimestamp && error.Path == "$.contextCapturedAtUtc");
        Assert.Contains(GovernedLoopSequentialContractValidator.Validate(missingBindingValues).Errors, error => error.Code == GovernedLoopSequentialValidationErrorCode.InvalidComposition && error.Path == "$.executionBinding");
        Assert.Contains(GovernedLoopSequentialContractValidator.Validate(futureConversation).Errors, error => error.Code == GovernedLoopSequentialValidationErrorCode.InvalidTimestamp && error.Path == "$.invokingConversation.capturedAtUtc");
        Assert.Contains(GovernedLoopSequentialContractValidator.Validate(validSnapshot with { ContentHash = Hash('f') }).Errors, error => error.Code == GovernedLoopSequentialValidationErrorCode.HashMismatch && error.Path == "$.contentHash");
        Assert.Contains(GovernedLoopSequentialContractValidator.Validate(validBinding with { ContentHash = Hash('f') }).Errors, error => error.Code == GovernedLoopSequentialValidationErrorCode.HashMismatch && error.Path == "$.contentHash");
        Assert.False(GovernedLoopSequentialContractHash.Matches(validSnapshot with { ContentHash = "bad" }));
        Assert.False(GovernedLoopSequentialContractHash.Matches(validBinding with { ContentHash = "bad" }));
        Assert.False(GovernedLoopSequentialContractHash.Matches(missingSnapshotValues));
    }

    [Fact]
    public void Context_manifest_rejects_null_order_enum_duplicate_time_count_omission_and_truncation_substitutions()
    {
        var valid = Snapshot();
        var candidates = new List<(GovernedLoopSequentialInvocationSnapshot Snapshot, GovernedLoopSequentialValidationErrorCode Code, string Path)>();
        var nullSource = valid.ContextManifest.ToArray();
        nullSource[0] = null!;
        candidates.Add((CopySnapshot(valid, contextManifest: nullSource), GovernedLoopSequentialValidationErrorCode.Required, "$.contextManifest[0]"));
        var wrongOrder = valid.ContextManifest.ToArray();
        wrongOrder[0] = wrongOrder[0] with { Order = 2 };
        candidates.Add((CopySnapshot(valid, contextManifest: wrongOrder), GovernedLoopSequentialValidationErrorCode.InvalidComposition, "$.contextManifest[0].order"));
        var undefined = valid.ContextManifest.ToArray();
        undefined[0] = undefined[0] with { SourceType = CustomLoopContextSource.Unknown };
        candidates.Add((CopySnapshot(valid, contextManifest: undefined), GovernedLoopSequentialValidationErrorCode.InvalidEnumeration, "$.contextManifest[0].sourceType"));
        var duplicate = valid.ContextManifest.ToArray();
        duplicate[1] = duplicate[1] with { SourceId = duplicate[0].SourceId };
        candidates.Add((CopySnapshot(valid, contextManifest: duplicate), GovernedLoopSequentialValidationErrorCode.InvalidComposition, "$.contextManifest[1].sourceId"));
        var wrongTime = valid.ContextManifest.ToArray();
        wrongTime[0] = wrongTime[0] with { CapturedAtUtc = _capturedAtUtc.AddSeconds(1) };
        candidates.Add((CopySnapshot(valid, contextManifest: wrongTime), GovernedLoopSequentialValidationErrorCode.InvalidTimestamp, "$.contextManifest[0].capturedAtUtc"));
        var wrongCount = valid.ContextManifest.ToArray();
        wrongCount[0] = wrongCount[0] with { OriginalCharacterCount = -1 };
        candidates.Add((CopySnapshot(valid, contextManifest: wrongCount), GovernedLoopSequentialValidationErrorCode.InvalidComposition, "$.contextManifest[0].usedCharacterCount"));
        var invalidOmission = valid.ContextManifest.ToArray();
        invalidOmission[0] = invalidOmission[0] with { Content = "x", ContentHash = CustomLoopTraceContentHash.Compute("x"), OriginalCharacterCount = 1, UsedCharacterCount = 1 };
        candidates.Add((CopySnapshot(valid, contextManifest: invalidOmission), GovernedLoopSequentialValidationErrorCode.InvalidComposition, "$.contextManifest[0]"));
        var invalidTruncation = valid.ContextManifest.ToArray();
        invalidTruncation[0] = invalidTruncation[0] with { Content = "x", ContentHash = CustomLoopTraceContentHash.Compute("x"), OriginalCharacterCount = 2, UsedCharacterCount = 1, OmissionReason = null };
        candidates.Add((CopySnapshot(valid, contextManifest: invalidTruncation), GovernedLoopSequentialValidationErrorCode.InvalidComposition, "$.contextManifest[0]"));

        foreach (var candidate in candidates)
        {
            Assert.Contains(GovernedLoopSequentialContractValidator.Validate(candidate.Snapshot).Errors, error => error.Code == candidate.Code && error.Path == candidate.Path);
        }
    }

    [Fact]
    public void Conversation_manifest_enforces_aggregate_source_omission_and_character_bounds()
    {
        var valid = Snapshot();
        var omittedTail = ConversationSource(8, "omitted-1", string.Empty, "History omitted.");
        var secondOmittedTail = ConversationSource(9, "omitted-2", string.Empty, "More history omitted.");
        var twoOmissions = CopySnapshot(valid, contextManifest: valid.ContextManifest.Concat([omittedTail, secondOmittedTail]).ToArray());
        var largeContent = new string('x', GovernedLoopSequentialContractLimits.MaxContextCharacters / 2 + 1);
        var tooManyCharacters = CopySnapshot(valid, contextManifest: valid.ContextManifest.Concat([
            ConversationSource(8, "large-1", largeContent, null),
            ConversationSource(9, "large-2", largeContent, null),
        ]).ToArray());
        var tooManySources = CopySnapshot(valid, contextManifest: valid.ContextManifest.Concat(
            Enumerable.Range(1, GovernedLoopSequentialContractLimits.MaxInvokingConversationSources + 1)
                .Select(index => ConversationSource(index + 7, $"message-{index:D3}", "x", null))).ToArray());

        Assert.Contains(GovernedLoopSequentialContractValidator.Validate(twoOmissions).Errors, error => error.Code == GovernedLoopSequentialValidationErrorCode.CollectionTooLarge && error.Path == "$.contextManifest");
        Assert.Contains(GovernedLoopSequentialContractValidator.Validate(tooManyCharacters).Errors, error => error.Code == GovernedLoopSequentialValidationErrorCode.CollectionTooLarge && error.Path == "$.contextManifest");
        Assert.Contains(GovernedLoopSequentialContractValidator.Validate(tooManySources).Errors, error => error.Code == GovernedLoopSequentialValidationErrorCode.CollectionTooLarge && error.Path == "$.contextManifest");
    }

    [Fact]
    public void Optional_model_conversation_and_valid_unicode_paths_remain_canonical()
    {
        var valid = Snapshot();
        var withoutOptionals = new GovernedLoopSequentialInvocationSnapshot(
            1,
            "Prompt \U0001F642",
            new CustomLoopModelSnapshot("provider", null),
            null,
            valid.ContextCapturedAtUtc,
            valid.ContextManifest,
            string.Empty);
        var applied = GovernedLoopSequentialContractHash.Apply(withoutOptionals);

        Assert.True(GovernedLoopSequentialContractValidator.Validate(applied).IsValid);
        Assert.False(GovernedLoopSequentialContractValidator.Validate(applied with { TriggerPrompt = "bad\udc00" }).IsValid);
    }

    [Fact]
    public void Validation_errors_have_stable_public_value_semantics()
    {
        var first = Assert.Single(GovernedLoopSequentialContractValidator.Validate((GovernedLoopSequentialInvocationSnapshot?)null).Errors);
        var second = Assert.Single(GovernedLoopSequentialContractValidator.Validate((GovernedLoopSequentialInvocationSnapshot?)null).Errors);

        Assert.Equal(first, second);
        Assert.True(first.Equals(second));
        Assert.True(first.Equals((object)second));
        Assert.False(first.Equals((GovernedLoopSequentialValidationError?)null));
        Assert.Equal(first.GetHashCode(), second.GetHashCode());
        Assert.Equal("Required at $", first.ToString());
    }

    private static GovernedLoopSequentialInvocationSnapshot Snapshot()
    {
        var context = CustomLoopContextSnapshot.CreateEmpty(_capturedAtUtc);
        return GovernedLoopSequentialContractHash.Apply(new GovernedLoopSequentialInvocationSnapshot(
            GovernedLoopSequentialInvocationSnapshot.CurrentSchemaVersion,
            "Answer the admitted request.",
            new CustomLoopModelSnapshot("provider", "model"),
            new CustomLoopConversationReference("conversation-1", "version-1", _capturedAtUtc.AddMinutes(-1)),
            _capturedAtUtc,
            context.SourceManifest,
            string.Empty));
    }

    private static GovernedLoopSequentialAdapterBinding AdapterBinding()
    {
        var execution = GovernedLoopExecutionBinding.Create(
            1,
            "run-1",
            GovernedLoopRevisionReference.Create(1, "graph-1", "revision-1", Hash('1')),
            1);
        return GovernedLoopSequentialContractHash.Apply(new GovernedLoopSequentialAdapterBinding(
            GovernedLoopSequentialAdapterBinding.CurrentSchemaVersion,
            WorkspaceId('a'),
            execution,
            "admit-1",
            Hash('2'),
            Hash('3'),
            Hash('4'),
            Hash('5'),
            Hash('6'),
            string.Empty));
    }

    private static GovernedLoopSequentialInvocationSnapshot CopySnapshot(
        GovernedLoopSequentialInvocationSnapshot source,
        CustomLoopModelSnapshot? model = null,
        CustomLoopConversationReference? conversation = null,
        IReadOnlyList<CustomLoopContextManifestSource>? contextManifest = null)
        => new(
            source.SchemaVersion,
            source.TriggerPrompt,
            model ?? source.ModelSnapshot,
            conversation ?? source.InvokingConversation,
            source.ContextCapturedAtUtc,
            contextManifest ?? source.ContextManifest,
            source.ContentHash);

    private static GovernedLoopSequentialAdapterBinding CopyBinding(
        GovernedLoopSequentialAdapterBinding source,
        GovernedLoopExecutionBinding execution,
        string? operationId = null)
        => new(
            source.SchemaVersion,
            source.WorkspaceId,
            execution,
            operationId ?? source.AdmissionOperationId,
            source.AdmissionReceiptHash,
            source.AdmissionRequestHash,
            source.InvocationPayloadHash,
            source.GraphArtifactHash,
            source.GraphLayoutHash,
            source.ContentHash);

    private static CustomLoopContextManifestSource ConversationSource(
        int order,
        string sourceId,
        string content,
        string? omissionReason)
        => new(
            order,
            CustomLoopContextSource.InvokingConversation,
            sourceId,
            $"conversation/conversation-1/version-1/{sourceId}",
            CustomLoopContextProvenance.LogicalConversation,
            CustomLoopContextTrustClass.UntrustedData,
            LlmMessageRole.User,
            content,
            CustomLoopTraceContentHash.Compute(content),
            content.Length,
            content.Length,
            false,
            null,
            omissionReason,
            _capturedAtUtc);

    private static string WorkspaceId(char value) => "workspace-sha256:" + Hash(value);

    private static string Hash(char value) => new(value, GovernedLoopSequentialContractLimits.Sha256HexCharacters);
}
