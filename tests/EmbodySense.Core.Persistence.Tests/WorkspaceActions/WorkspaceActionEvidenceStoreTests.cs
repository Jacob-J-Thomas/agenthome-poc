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

    private static WorkspaceActionBeforeEvidence Before()
    {
        WorkspaceActionScopeId.TryParse("workspace", out var scope);
        WorkspaceRelativeFileTarget.TryParse("notes/file.txt", out var target, out _);
        return WorkspaceActionEvidenceContract.CreateBefore(
            scope!,
            target!,
            Hash('1'),
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
            DateTimeOffset.Parse("2026-08-12T20:00:00Z"));
    }

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
