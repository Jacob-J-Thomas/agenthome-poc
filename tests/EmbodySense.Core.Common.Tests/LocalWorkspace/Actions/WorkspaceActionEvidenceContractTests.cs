using EmbodySense.Core.Common.Governance.Permissions.Models;
using EmbodySense.Core.Common.LocalWorkspace.Actions;
using EmbodySense.Core.Common.LocalWorkspace.Actions.Models;

namespace EmbodySense.Core.Common.Tests.LocalWorkspace.Actions;

public sealed class WorkspaceActionEvidenceContractTests
{
    private static readonly DateTimeOffset _now = new(2026, 8, 12, 20, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Content_addressed_before_after_outcome_and_tombstone_records_validate_and_reject_tampering()
    {
        var before = Before();
        var quarantine = "quarantine-" + Hash('9');
        var tombstone = WorkspaceActionEvidenceContract.CreateTombstone(before, quarantine, "effect-alpha", "operation-alpha", 1, 4, _now, _now.AddDays(30));
        var after = WorkspaceActionEvidenceContract.CreateAfter(before, WorkspaceActionOperationIds.Delete, "effect-alpha", "operation-alpha", 1, WorkspaceActionEntryKind.Absent, null, null, 0, 0, 4, quarantine, tombstone.TombstoneReference, _now);
        var outcome = WorkspaceActionEvidenceContract.CreateOutcome(after);

        Assert.Null(WorkspaceActionEvidenceContract.ValidateBefore(before));
        Assert.Null(WorkspaceActionEvidenceContract.ValidateTombstone(tombstone));
        Assert.Null(WorkspaceActionEvidenceContract.ValidateAfter(after));
        Assert.Null(WorkspaceActionEvidenceContract.ValidateOutcome(outcome));
        Assert.Equal("before-", before.EvidenceId[..7]);
        Assert.Equal("after-", after.EvidenceId[..6]);
        Assert.Equal("outcome-", outcome.EvidenceId[..8]);
        Assert.NotEqual(after.EvidenceId, outcome.EvidenceId);
        Assert.Equal("tombstone-", tombstone.TombstoneReference[..10]);
        Assert.NotNull(WorkspaceActionEvidenceContract.ValidateBefore(before with { ByteCount = 4 }));
        Assert.NotNull(WorkspaceActionEvidenceContract.ValidateAfter(after with { TargetReference = "other.txt" }));
        Assert.NotNull(WorkspaceActionEvidenceContract.ValidateOutcome(outcome with { AfterEvidenceHash = Hash('0') }));
        Assert.NotNull(WorkspaceActionEvidenceContract.ValidateTombstone(tombstone with { QuarantineReference = "other" }));
    }

    [Fact]
    public void Operation_specific_successors_reject_version_time_and_append_accounting_drift()
    {
        var before = Before();

        Assert.Throws<ArgumentException>(() => WorkspaceActionEvidenceContract.CreateAfter(
            before,
            WorkspaceActionOperationIds.Write,
            "effect-alpha",
            "operation-alpha",
            1,
            WorkspaceActionEntryKind.RegularFile,
            Hash('7'),
            Hash('8'),
            4,
            0,
            before.GovernedVersion,
            null,
            null,
            _now));
        Assert.Throws<ArgumentException>(() => WorkspaceActionEvidenceContract.CreateAfter(
            before,
            WorkspaceActionOperationIds.Append,
            "effect-alpha",
            "operation-alpha",
            1,
            WorkspaceActionEntryKind.RegularFile,
            Hash('7'),
            Hash('8'),
            5,
            1,
            before.GovernedVersion + 1,
            null,
            null,
            _now.AddTicks(-1)));
    }

    [Fact]
    public void Absent_before_and_regular_after_shapes_are_closed()
    {
        Assert.True(WorkspaceActionScopeId.TryParse("workspace", out var scope));
        Assert.True(WorkspaceRelativeFileTarget.TryParse("new.txt", out var target, out _));
        var absent = WorkspaceActionEvidenceContract.CreateBefore(scope!, target!, Hash('1'), Hash('2'), WorkspaceActionEntryKind.Absent, FileSystemOperation.Create, Hash('3'), Hash('4'), Hash('5'), null, null, 0, 0, _now);
        var after = WorkspaceActionEvidenceContract.CreateAfter(absent, WorkspaceActionOperationIds.Write, "effect-new", "operation-new", 1, WorkspaceActionEntryKind.RegularFile, Hash('5'), Hash('6'), 0, 0, 1, null, null, _now);

        Assert.Null(WorkspaceActionEvidenceContract.ValidateBefore(absent));
        Assert.Null(WorkspaceActionEvidenceContract.ValidateAfter(after));
        Assert.NotNull(WorkspaceActionEvidenceContract.ValidateBefore(absent with { ContentHash = Hash('7') }));
        Assert.NotNull(WorkspaceActionEvidenceContract.ValidateAfter(after with { QuarantineReference = "quarantine" }));
    }

    [Fact]
    public void Fingerprints_are_domain_separated_and_canonical()
    {
        var left = WorkspaceActionFingerprint.Compute("domain-a", "one", null, string.Empty);
        var right = WorkspaceActionFingerprint.Compute("domain-b", "one", null, string.Empty);
        var reordered = WorkspaceActionFingerprint.Compute("domain-a", "one", string.Empty, null);

        Assert.True(WorkspaceActionFingerprint.IsCanonicalSha256(left));
        Assert.NotEqual(left, right);
        Assert.NotEqual(left, reordered);
        Assert.False(WorkspaceActionFingerprint.IsCanonicalSha256(left.ToUpperInvariant()));
        Assert.True(WorkspaceActionFingerprint.IsEvidenceIdentifier("after-alpha/1"));
        Assert.False(WorkspaceActionFingerprint.IsEvidenceIdentifier("INVALID"));
    }

    private static WorkspaceActionBeforeEvidence Before()
    {
        Assert.True(WorkspaceActionScopeId.TryParse("workspace", out var scope));
        Assert.True(WorkspaceRelativeFileTarget.TryParse("notes/file.txt", out var target, out _));
        return WorkspaceActionEvidenceContract.CreateBefore(scope!, target!, Hash('1'), Hash('2'), WorkspaceActionEntryKind.RegularFile, FileSystemOperation.Modify, Hash('3'), Hash('4'), Hash('5'), Hash('6'), Hash('7'), 3, 3, _now);
    }

    private static string Hash(char value) => new(value, 64);
}
