using EmbodySense.Core.Common.Capabilities;
using EmbodySense.Core.Common.Capabilities.Models;
using EmbodySense.Core.Common.CommandActions;
using EmbodySense.Core.Common.CommandActions.Models;
using EmbodySense.Core.Common.Workspace;
using EmbodySense.Core.Persistence.CommandActions;
using EmbodySense.Tests.Support;

namespace EmbodySense.Core.Persistence.Tests.CommandActions;

public sealed class CommandActionEvidenceStoreTests
{
    [Fact]
    public async Task Immutable_evidence_exactly_replays_after_store_restart()
    {
        using var workspace = new TestWorkspace();
        var paths = new WorkspacePaths(workspace.RootPath);
        var before = Before();
        var outcome = Outcome(before, "operation-alpha", CommandActionOutcomeKind.Succeeded);
        var store = new CommandActionEvidenceStore(paths);

        await store.RetainPreparationAsync(before);
        await store.RetainPreparationAsync(before);
        await store.RetainOutcomeAsync(outcome);
        await store.RetainOutcomeAsync(outcome);

        var restarted = new CommandActionEvidenceStore(paths);
        Assert.Equal(before, await restarted.ReadPreparationAsync(before.EvidenceId));
        Assert.Equal(outcome, await restarted.ReadOutcomeAsync(outcome.EvidenceId));
        Assert.Equal(outcome, await restarted.ReadOutcomeByOperationAsync("operation-alpha", 1));
        Assert.Null(await restarted.ReadPreparationAsync("alias/../" + before.EvidenceId));
        Assert.Null(await restarted.ReadOutcomeByOperationAsync("bad path", 1));
        Assert.Single(Directory.EnumerateFiles(Root(paths, "preparations"), "*.json"));
        Assert.Single(Directory.EnumerateFiles(Root(paths, "outcomes"), "*.json"));
    }

    [Fact]
    public async Task Conflicting_outcome_for_one_operation_generation_is_rejected_without_overwrite()
    {
        using var workspace = new TestWorkspace();
        var paths = new WorkspacePaths(workspace.RootPath);
        var before = Before();
        var first = Outcome(before, "operation-alpha", CommandActionOutcomeKind.Succeeded);
        var conflict = Outcome(before, "operation-alpha", CommandActionOutcomeKind.NonZeroExit);
        var store = new CommandActionEvidenceStore(paths);
        await store.RetainOutcomeAsync(first);

        await Assert.ThrowsAsync<FormatException>(() => store.RetainOutcomeAsync(conflict));
        Assert.Equal(first, await store.ReadOutcomeByOperationAsync("operation-alpha", 1));
    }

    [Fact]
    public async Task Truncation_tamper_and_unsupported_artifacts_fail_closed()
    {
        using var workspace = new TestWorkspace();
        var paths = new WorkspacePaths(workspace.RootPath);
        var before = Before();
        var store = new CommandActionEvidenceStore(paths);
        await store.RetainPreparationAsync(before);
        var path = Path.Combine(Root(paths, "preparations"), before.EvidenceId + ".json");
        await File.WriteAllTextAsync(path, "{\"schemaVersion\":1");

        await Assert.ThrowsAsync<FormatException>(() => new CommandActionEvidenceStore(paths).ReadPreparationAsync(before.EvidenceId));

        File.Delete(path);
        var outcome = Outcome(before, "operation-alpha", CommandActionOutcomeKind.Succeeded);
        await store.RetainOutcomeAsync(outcome);
        await File.WriteAllTextAsync(Path.Combine(Root(paths, "outcomes"), "unsupported.tmp"), "value");
        await Assert.ThrowsAsync<FormatException>(() => new CommandActionEvidenceStore(paths).ReadOutcomeByOperationAsync("operation-alpha", 1));
    }

    [Fact]
    public async Task Persisted_evidence_contains_no_host_path_or_unredacted_canary()
    {
        using var workspace = new TestWorkspace();
        var paths = new WorkspacePaths(workspace.RootPath);
        var before = Before();
        var outcome = Outcome(before, "operation-alpha", CommandActionOutcomeKind.NonZeroExit);
        var store = new CommandActionEvidenceStore(paths);
        await store.RetainPreparationAsync(before);
        await store.RetainOutcomeAsync(outcome);

        var persisted = string.Join('\n', Directory.EnumerateFiles(Root(paths, "preparations"), "*.json")
            .Concat(Directory.EnumerateFiles(Root(paths, "outcomes"), "*.json"))
            .Select(File.ReadAllText));
        Assert.DoesNotContain(workspace.RootPath, persisted, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("secret-canary", persisted, StringComparison.Ordinal);
    }

    private static CommandActionPreparationEvidence Before()
        => CommandActionEvidenceContract.CreatePreparation(
            Template(), new string('1', 64), new string('2', 64), new string('3', 64),
            new DateTimeOffset(2026, 8, 13, 12, 0, 0, TimeSpan.Zero));

    private static CommandActionOutcomeEvidence Outcome(
        CommandActionPreparationEvidence before,
        string operationId,
        CommandActionOutcomeKind kind)
        => CommandActionEvidenceContract.CreateOutcome(
            "effect-alpha", operationId, 1, Template(), before.InputFingerprint, before.TargetFingerprint,
            before.PreconditionEvidenceHash, before.EvidenceId,
            kind,
            CommandActionTerminationPosture.Exited,
            kind == CommandActionOutcomeKind.Succeeded ? 0 : 7,
            kind == CommandActionOutcomeKind.Succeeded ? "{\"ok\":true}" : null,
            kind == CommandActionOutcomeKind.Succeeded ? null : "[redacted]",
            11, 0, 25,
            new DateTimeOffset(2026, 8, 13, 12, 0, 1, TimeSpan.Zero));

    private static CommandActionTemplate Template()
    {
        Assert.True(CapabilityId.TryParse("org.example/command", out var id, out _));
        Assert.True(CapabilityVersion.TryParse("1.0.0", out var version, out _));
        Assert.True(CapabilityDescriptorHash.TryParse("sha256:" + new string('a', 64), out var hash, out _));
        Assert.True(CapabilityProviderId.TryParse("org.example", out var provider, out _));
        Assert.True(CapabilityIntegrityDigest.TryParse("sha256:" + new string('b', 64), out var digest, out _));
        return CommandActionTemplateContract.Create(
            1,
            new CapabilityDescriptorIdentity(id!, version!, hash!),
            new CapabilityImplementationIdentity(provider!, "command/runner"),
            digest!, 3, "command/render", 1, [], [], [],
            CommandActionSecondaryGrammarPolicy.None,
            CommandActionStandardInputKind.Closed, null, CommandActionOutputKind.Json,
            new CommandActionIsolationPolicy(CommandActionWorkingDirectoryKind.ArtifactRoot, CommandActionNetworkPolicy.Denied, 5_000, 2_000, 64_000_000, 16_384, 1, true),
            false);
    }

    private static string Root(WorkspacePaths paths, string kind)
        => Path.Combine(paths.AgentPath, "loops", "execution", "command-actions", kind);
}
