using EmbodySense.Core.Common.Governance.Permissions.Models;
using EmbodySense.Core.Common.LocalWorkspace.Actions;
using EmbodySense.Core.Common.LocalWorkspace.Actions.Models;
using EmbodySense.Core.Common.Workspace;
using EmbodySense.Core.Persistence.WorkspaceActions;
using EmbodySense.Tests.Support;

namespace EmbodySense.Core.Persistence.Tests.WorkspaceActions;

public sealed class WorkspaceActionEvidenceStoreTests
{
    [Fact]
    public async Task ImmutableEvidenceExactlyReplaysAcrossStoreRestart()
    {
        using var workspace = new TestWorkspace();
        var paths = new WorkspacePaths(workspace.RootPath);
        var before = Before();
        var after = After(before, WorkspaceActionOperationIds.Write, Hash('8'));
        var outcome = WorkspaceActionEvidenceContract.CreateOutcome(after);
        var store = new WorkspaceActionEvidenceStore(paths);

        await store.RetainBeforeAsync(before);
        await store.RetainBeforeAsync(before);
        await store.RetainAfterAsync(after);
        await store.RetainAfterAsync(after);
        await store.RetainOutcomeAsync(outcome);
        await store.RetainOutcomeAsync(outcome);

        var restarted = new WorkspaceActionEvidenceStore(paths);
        Assert.Equal(before, await restarted.ReadBeforeAsync(before.EvidenceId));
        Assert.Equal(after, await restarted.ReadAfterAsync(after.EvidenceId));
        Assert.Equal(after, await restarted.FindAfterAsync("effect-alpha", "operation-alpha", 1));
        Assert.Equal(outcome, await restarted.ReadOutcomeAsync(outcome.EvidenceId));
        Assert.Equal(outcome, await restarted.FindOutcomeAsync("effect-alpha", "operation-alpha", 1));
        Assert.Single(Directory.EnumerateFiles(Root(paths, "before"), "*.json"));
        Assert.Single(Directory.EnumerateFiles(Root(paths, "after"), "*.json"));
        Assert.Single(Directory.EnumerateFiles(Root(paths, "outcomes"), "*.json"));
    }

    [Fact]
    public async Task EvidenceLookupRejectsPathAliasesInsteadOfResolvingThemInsideTheStore()
    {
        using var workspace = new TestWorkspace();
        var paths = new WorkspacePaths(workspace.RootPath);
        var before = Before();
        var after = After(before, WorkspaceActionOperationIds.Write, Hash('8'));
        var store = new WorkspaceActionEvidenceStore(paths);
        await store.RetainBeforeAsync(before);
        await store.RetainAfterAsync(after);
        var outcome = WorkspaceActionEvidenceContract.CreateOutcome(after);
        await store.RetainOutcomeAsync(outcome);

        Assert.Null(await store.ReadBeforeAsync("alias/../" + before.EvidenceId));
        Assert.Null(await store.ReadAfterAsync("alias/../" + after.EvidenceId));
        Assert.Null(await store.ReadOutcomeAsync("alias/../" + outcome.EvidenceId));
    }

    [Fact]
    public async Task TamperTruncationAndUnsupportedArtifactsFailClosed()
    {
        using var workspace = new TestWorkspace();
        var paths = new WorkspacePaths(workspace.RootPath);
        var before = Before();
        var store = new WorkspaceActionEvidenceStore(paths);
        await store.RetainBeforeAsync(before);
        var path = Path.Combine(Root(paths, "before"), before.EvidenceId + ".json");
        await File.WriteAllTextAsync(path, "{\"schemaVersion\":1");

        await Assert.ThrowsAsync<FormatException>(() => new WorkspaceActionEvidenceStore(paths).ReadBeforeAsync(before.EvidenceId));

        File.Delete(path);
        await File.WriteAllTextAsync(Path.Combine(Root(paths, "before"), "unsupported.tmp"), "value");
        await Assert.ThrowsAsync<FormatException>(() => new WorkspaceActionEvidenceStore(paths).FindBeforeStateAsync(
            before.ScopeId,
            before.TargetReference,
            before.TargetFingerprint,
            before.PreconditionEvidenceHash,
            before.EntryKind,
            before.PermissionOperation,
            before.PermissionPolicyHash,
            before.RootIdentityFingerprint,
            before.ParentIdentityFingerprint,
            before.NativeIdentityFingerprint,
            before.ContentHash,
            before.ByteCount,
            before.GovernedVersion));
    }

    [Fact]
    public async Task ExactAtomicWriteTemporaryFromProcessLossIsAuthenticatedAndRemoved()
    {
        using var workspace = new TestWorkspace();
        var paths = new WorkspacePaths(workspace.RootPath);
        var before = Before();
        var store = new WorkspaceActionEvidenceStore(paths);
        await store.RetainBeforeAsync(before);
        var temporary = Path.Combine(
            Root(paths, "before"),
            $".{before.EvidenceId}.json.{new string('a', 32)}.tmp");
        await File.WriteAllTextAsync(temporary, "partial");
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(temporary, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }

        Assert.Equal(before, await new WorkspaceActionEvidenceStore(paths).ReadBeforeAsync(before.EvidenceId));
        Assert.False(File.Exists(temporary));
        Assert.Single(Directory.EnumerateFiles(Root(paths, "before"), "*.json"));
    }

    [Fact]
    public async Task ConflictingOutcomesForOneEffectGenerationAreCorrupt()
    {
        using var workspace = new TestWorkspace();
        var store = new WorkspaceActionEvidenceStore(new WorkspacePaths(workspace.RootPath));
        var before = Before();
        await store.RetainBeforeAsync(before);
        await store.RetainAfterAsync(After(before, WorkspaceActionOperationIds.Write, Hash('8')));
        await store.RetainAfterAsync(After(before, WorkspaceActionOperationIds.Append, Hash('9')));

        await Assert.ThrowsAsync<FormatException>(() => store.FindAfterAsync("effect-alpha", "operation-alpha", 1));
    }

    [Fact]
    public async Task PersistedEvidenceIsValueFreeAndContainsNoAbsoluteHostPath()
    {
        using var workspace = new TestWorkspace();
        var paths = new WorkspacePaths(workspace.RootPath);
        var store = new WorkspaceActionEvidenceStore(paths);
        var before = Before();
        var after = After(before, WorkspaceActionOperationIds.Write, Hash('8'));
        var outcome = WorkspaceActionEvidenceContract.CreateOutcome(after);
        await store.RetainBeforeAsync(before);
        await store.RetainAfterAsync(after);
        await store.RetainOutcomeAsync(outcome);

        var persisted = string.Join('\n', Directory.EnumerateFiles(Root(paths, "before"), "*.json").Concat(
            Directory.EnumerateFiles(Root(paths, "after"), "*.json")).Concat(
            Directory.EnumerateFiles(Root(paths, "outcomes"), "*.json")).Select(File.ReadAllText));
        Assert.DoesNotContain("literal-secret-canary", persisted, StringComparison.Ordinal);
        Assert.DoesNotContain(workspace.RootPath, persisted, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("notes/file.txt", persisted, StringComparison.Ordinal);
    }

    [Fact]
    public async Task LocalQuotaCanOnlyNarrowCanonicalBoundsAndRejectsNewEvidenceAtCapacity()
    {
        using var workspace = new TestWorkspace();
        var paths = new WorkspacePaths(workspace.RootPath);
        var quota = new WorkspaceActionStorageLimits(1, 1, 1, 1);
        var store = new WorkspaceActionEvidenceStore(paths, quota);
        var first = Before();
        WorkspaceActionScopeId.TryParse(first.ScopeId, out var scope);
        WorkspaceRelativeFileTarget.TryParse(first.TargetReference, out var target, out _);
        var second = WorkspaceActionEvidenceContract.CreateBefore(
            scope!,
            target!,
            first.TargetFingerprint,
            first.PreconditionEvidenceHash,
            first.EntryKind,
            first.PermissionOperation,
            first.PermissionPolicyHash,
            first.RootIdentityFingerprint,
            first.ParentIdentityFingerprint,
            first.NativeIdentityFingerprint,
            first.ContentHash,
            first.ByteCount,
            first.GovernedVersion,
            first.CapturedAtUtc.AddSeconds(1));

        await store.RetainBeforeAsync(first);

        await Assert.ThrowsAnyAsync<InvalidOperationException>(() => store.RetainBeforeAsync(second));
        Assert.Throws<ArgumentOutOfRangeException>(() => new WorkspaceActionEvidenceStore(
            paths,
            quota with { MaximumEvidenceRecordsPerKind = WorkspaceActionContractLimits.MaxEvidenceRecordsPerKind + 1 }));
    }

    [Fact]
    public async Task Invalid_identifiers_and_exact_state_queries_fail_closed()
    {
        using var workspace = new TestWorkspace();
        var store = new WorkspaceActionEvidenceStore(new WorkspacePaths(workspace.RootPath));

        Assert.Null(await store.ReadBeforeAsync(null!));
        Assert.Null(await store.ReadBeforeAsync("before-not-a-hash"));
        Assert.Null(await store.ReadAfterAsync("after-../alias"));
        Assert.Null(await store.ReadOutcomeAsync("outcome-"));
        Assert.Null(await store.ReadTombstoneAsync("tombstone-"));
        Assert.Null(await store.FindAfterAsync("bad", "operation-alpha", 1));
        Assert.Null(await store.FindAfterAsync("effect-alpha", "bad", 1));
        Assert.Null(await store.FindAfterAsync("effect-alpha", "operation-alpha", 0));
        Assert.Null(await store.FindOutcomeAsync("bad", "operation-alpha", 1));
        Assert.False(await store.IsUniqueTargetReferenceAsync("bad", "notes/file.txt", Hash('4'), Hash('5'), null));
        Assert.False(await store.IsUniqueTargetReferenceAsync(Hash('1'), "../file.txt", Hash('4'), Hash('5'), null));
        await Assert.ThrowsAsync<ArgumentException>(() => store.RetainBeforeAsync(Before() with { EvidenceId = "invalid" }));
    }

    [Fact]
    public async Task Before_state_and_target_alias_queries_require_exact_identity()
    {
        using var workspace = new TestWorkspace();
        var paths = new WorkspacePaths(workspace.RootPath);
        var before = Before();
        var alias = Before("notes/alias.txt", before.TargetFingerprint, before.CapturedAtUtc.AddSeconds(1));
        var store = new WorkspaceActionEvidenceStore(paths);
        await store.RetainBeforeAsync(before);
        await store.RetainBeforeAsync(alias);

        Assert.Equal(before, await store.FindBeforeStateAsync(
            before.ScopeId,
            before.TargetReference,
            before.TargetFingerprint,
            before.PreconditionEvidenceHash,
            before.EntryKind,
            before.PermissionOperation,
            before.PermissionPolicyHash,
            before.RootIdentityFingerprint,
            before.ParentIdentityFingerprint,
            before.NativeIdentityFingerprint,
            before.ContentHash,
            before.ByteCount,
            before.GovernedVersion));
        Assert.Null(await store.FindBeforeStateAsync(
            before.ScopeId,
            before.TargetReference,
            before.TargetFingerprint,
            before.PreconditionEvidenceHash,
            before.EntryKind,
            before.PermissionOperation,
            before.PermissionPolicyHash,
            before.RootIdentityFingerprint,
            before.ParentIdentityFingerprint,
            before.NativeIdentityFingerprint,
            before.ContentHash,
            before.ByteCount + 1,
            before.GovernedVersion));
        Assert.False(await store.IsUniqueTargetReferenceAsync(
            before.TargetFingerprint,
            before.TargetReference,
            before.RootIdentityFingerprint,
            before.ParentIdentityFingerprint,
            before.NativeIdentityFingerprint));
        Assert.True(await store.IsUniqueTargetReferenceAsync(
            Hash('a'),
            "notes/other.txt",
            Hash('b'),
            Hash('c'),
            Hash('d')));
    }

    [Fact]
    public async Task Tombstones_replay_across_restart_and_enforce_their_independent_quota()
    {
        using var workspace = new TestWorkspace();
        var paths = new WorkspacePaths(workspace.RootPath);
        var before = Before();
        var tombstone = Tombstone(before, "quarantine-" + Hash('a'));
        var store = new WorkspaceActionEvidenceStore(paths);
        await store.RetainTombstoneAsync(tombstone);
        await store.RetainTombstoneAsync(tombstone);

        var restarted = new WorkspaceActionEvidenceStore(paths);
        Assert.Equal(tombstone, await restarted.ReadTombstoneAsync(tombstone.TombstoneReference));
        Assert.Null(await restarted.ReadTombstoneAsync("tombstone-" + Hash('f')));

        var limited = new WorkspaceActionEvidenceStore(paths, new WorkspaceActionStorageLimits(64, 64, 1, 1_000_000));
        var second = Tombstone(Before("notes/second.txt", Hash('e')), "quarantine-" + Hash('b'));
        await Assert.ThrowsAnyAsync<InvalidOperationException>(() => limited.RetainTombstoneAsync(second));
        Assert.Throws<ArgumentOutOfRangeException>(() => new WorkspaceActionEvidenceStore(
            paths,
            new WorkspaceActionStorageLimits(64, 64, WorkspaceActionContractLimits.MaxTombstones + 1, 1_000_000)));
    }

    [Theory]
    [InlineData("before")]
    [InlineData("after")]
    [InlineData("outcomes")]
    [InlineData("tombstones")]
    public async Task Unsupported_artifacts_in_each_evidence_kind_fail_closed(string kind)
    {
        using var workspace = new TestWorkspace();
        var paths = new WorkspacePaths(workspace.RootPath);
        var before = Before();
        var store = new WorkspaceActionEvidenceStore(paths);
        var root = Root(paths, kind);
        Directory.CreateDirectory(root);

        Func<Task> read;
        switch (kind)
        {
            case "before":
                await store.RetainBeforeAsync(before);
                read = () => store.ReadBeforeAsync(before.EvidenceId);
                break;
            case "after":
                var after = After(before, WorkspaceActionOperationIds.Write, Hash('8'));
                await store.RetainAfterAsync(after);
                read = () => store.ReadAfterAsync(after.EvidenceId);
                break;
            case "outcomes":
                var outcome = WorkspaceActionEvidenceContract.CreateOutcome(After(before, WorkspaceActionOperationIds.Write, Hash('8')));
                await store.RetainOutcomeAsync(outcome);
                read = () => store.ReadOutcomeAsync(outcome.EvidenceId);
                break;
            case "tombstones":
                var tombstone = Tombstone(before, "quarantine-" + Hash('a'));
                await store.RetainTombstoneAsync(tombstone);
                read = () => store.ReadTombstoneAsync(tombstone.TombstoneReference);
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(kind));
        }

        await File.WriteAllTextAsync(Path.Combine(root, "not-supported.bin"), "value");
        await Assert.ThrowsAsync<FormatException>(read);
    }

    [Fact]
    public async Task Exceeding_record_bound_is_detected_before_reading_content()
    {
        using var workspace = new TestWorkspace();
        var paths = new WorkspacePaths(workspace.RootPath);
        var store = new WorkspaceActionEvidenceStore(paths, new WorkspaceActionStorageLimits(1, 1, 1, 1));
        var first = Before();
        var second = Before("notes/second.txt", Hash('e'));
        await store.RetainBeforeAsync(first);
        var root = Root(paths, "before");
        File.Copy(
            Path.Combine(root, first.EvidenceId + ".json"),
            Path.Combine(root, second.EvidenceId + ".json"));

        await Assert.ThrowsAsync<FormatException>(() => new WorkspaceActionEvidenceStore(
            paths,
            new WorkspaceActionStorageLimits(1, 1, 1, 1_000_000)).ReadBeforeAsync(first.EvidenceId));
    }

    private static WorkspaceActionBeforeEvidence Before(
        string targetReference = "notes/file.txt",
        string? targetFingerprint = null,
        DateTimeOffset? capturedAtUtc = null)
    {
        WorkspaceActionScopeId.TryParse("workspace", out var scope);
        WorkspaceRelativeFileTarget.TryParse(targetReference, out var target, out _);
        return WorkspaceActionEvidenceContract.CreateBefore(
            scope!,
            target!,
            targetFingerprint ?? Hash('1'),
            Hash('2'),
            WorkspaceActionEntryKind.RegularFile,
            FileSystemOperation.Modify,
            Hash('3'),
            Hash('4'),
            Hash('5'),
            Hash('6'),
            Hash('7'),
            7,
            0,
            capturedAtUtc ?? DateTimeOffset.Parse("2026-08-12T20:00:00Z"));
    }

    private static WorkspaceActionTombstone Tombstone(WorkspaceActionBeforeEvidence before, string quarantineReference)
        => WorkspaceActionEvidenceContract.CreateTombstone(
            before,
            quarantineReference,
            "effect-alpha",
            "operation-alpha",
            1,
            1,
            before.CapturedAtUtc.AddSeconds(1),
            before.CapturedAtUtc.AddHours(1));

    private static WorkspaceActionAfterEvidence After(
        WorkspaceActionBeforeEvidence before,
        string operationId,
        string contentHash)
        => WorkspaceActionEvidenceContract.CreateAfter(
            before,
            operationId,
            "effect-alpha",
            "operation-alpha",
            1,
            WorkspaceActionEntryKind.RegularFile,
            Hash('7'),
            contentHash,
            8,
            operationId == WorkspaceActionOperationIds.Append ? 1 : 0,
            1,
            null,
            null,
            DateTimeOffset.Parse("2026-08-12T20:00:01Z"));

    private static string Root(WorkspacePaths paths, string kind)
        => Path.Combine(paths.AgentPath, "loops", "execution", "workspace-actions", kind);

    private static string Hash(char value) => new(value, 64);
}
