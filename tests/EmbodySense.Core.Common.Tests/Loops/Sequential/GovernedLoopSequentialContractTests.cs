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
        GovernedLoopExecutionBinding execution)
        => new(
            source.SchemaVersion,
            source.WorkspaceId,
            execution,
            source.AdmissionOperationId,
            source.AdmissionReceiptHash,
            source.AdmissionRequestHash,
            source.InvocationPayloadHash,
            source.GraphArtifactHash,
            source.GraphLayoutHash,
            source.ContentHash);

    private static string WorkspaceId(char value) => "workspace-sha256:" + Hash(value);

    private static string Hash(char value) => new(value, GovernedLoopSequentialContractLimits.Sha256HexCharacters);
}
