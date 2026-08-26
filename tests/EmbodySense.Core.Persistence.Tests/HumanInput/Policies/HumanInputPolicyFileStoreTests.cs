using EmbodySense.Core.Application.HumanInput.Policies.Models;
using EmbodySense.Core.Common.Loops.HumanInput.Policies;
using EmbodySense.Core.Common.Loops.HumanInput.Policies.Models;
using EmbodySense.Core.Common.Workspace;
using EmbodySense.Core.Persistence.HumanInput.Policies;
using EmbodySense.Core.Persistence.HumanInput.Policies.Models;
using EmbodySense.Tests.Support;

namespace EmbodySense.Core.Persistence.Tests.HumanInput.Policies;

public sealed class HumanInputPolicyFileStoreTests
{
    [Fact]
    public async Task Exact_immutable_policy_write_restart_read_and_replay_preserve_identity_and_hash()
    {
        using var workspace = new TestWorkspace();
        var paths = new WorkspacePaths(workspace.RootPath);
        var policy = Timeout();
        var committed = await new HumanInputPolicyFileStore(paths).CommitAsync(policy, 0);
        var read = await new HumanInputPolicyFileStore(paths).ReadAsync(policy.Reference);
        var replay = await new HumanInputPolicyFileStore(paths).CommitAsync(policy, 1);

        Assert.Equal(HumanInputPolicyFileStoreWriteStatus.Committed, committed.Status);
        Assert.Equal(1, committed.StoreGeneration);
        Assert.Equal(HumanInputPolicySourceReadStatus.Ready, read.Status);
        Assert.Equal(policy, read.Policy);
        Assert.Equal(HumanInputPolicyFileStoreWriteStatus.Replayed, replay.Status);
        Assert.Equal(1, replay.StoreGeneration);
    }

    [Fact]
    public async Task Stale_divergent_missing_and_malformed_artifacts_fail_closed_without_default_selection()
    {
        using var workspace = new TestWorkspace();
        var paths = new WorkspacePaths(workspace.RootPath);
        var store = new HumanInputPolicyFileStore(paths);
        var timeout = Timeout();
        var committed = await store.CommitAsync(timeout, 0);
        var stale = await store.CommitAsync(Failure(), 0);
        var divergent = await store.CommitAsync(HumanInputPolicyArtifactHash.Apply(timeout with { ResponseWindowMilliseconds = 120_000 }), 1);
        var missing = await store.ReadAsync(new HumanInputPolicyReference("timeout-one", "revision-two"));
        var malformed = await store.CommitAsync(timeout with { ContentHash = new string('a', 64) }, 1);

        Assert.Equal(HumanInputPolicyFileStoreWriteStatus.Committed, committed.Status);
        Assert.Equal(HumanInputPolicyFileStoreWriteStatus.Conflict, stale.Status);
        Assert.Equal(HumanInputPolicyFileStoreWriteStatus.Invalid, divergent.Status);
        Assert.Equal(HumanInputPolicySourceReadStatus.NotFound, missing.Status);
        Assert.Equal(HumanInputPolicyFileStoreWriteStatus.Invalid, malformed.Status);
    }

    [Fact]
    public async Task Hostile_persisted_bytes_are_unavailable_to_a_separate_source_instance()
    {
        using var workspace = new TestWorkspace();
        var paths = new WorkspacePaths(workspace.RootPath);
        var policy = Timeout();
        var store = new HumanInputPolicyFileStore(paths);
        Assert.Equal(HumanInputPolicyFileStoreWriteStatus.Committed, (await store.CommitAsync(policy, 0)).Status);

        var path = Path.Combine(paths.AgentPath, "human-input", "policies", "timeout-one@revision-one.json");
        await File.WriteAllTextAsync(path, "{\"unsupported\":true}");

        var result = await new HumanInputPolicyFileStore(paths).ReadAsync(policy.Reference);

        Assert.Equal(HumanInputPolicySourceReadStatus.Unavailable, result.Status);
        Assert.Null(result.Policy);
    }

    [Fact]
    public async Task Missing_or_divergent_catalog_generation_is_unavailable()
    {
        using var workspace = new TestWorkspace();
        var paths = new WorkspacePaths(workspace.RootPath);
        var policy = Timeout();
        var store = new HumanInputPolicyFileStore(paths);
        Assert.Equal(HumanInputPolicyFileStoreWriteStatus.Committed, (await store.CommitAsync(policy, 0)).Status);

        var generationPath = Path.Combine(paths.AgentPath, "human-input", "policies", "generation");
        await File.WriteAllTextAsync(generationPath, "0");

        var result = await new HumanInputPolicyFileStore(paths).ReadAsync(policy.Reference);

        Assert.Equal(HumanInputPolicySourceReadStatus.Unavailable, result.Status);
    }

    private static HumanInputPolicyArtifact Timeout()
        => HumanInputPolicyArtifactHash.Apply(new HumanInputPolicyArtifact(1, "timeout-one", "revision-one", HumanInputPolicyKind.ResponseWindow, "workspace-one", "graph-one", "actor-one", 3_600_000, HumanInputTerminalDisposition.Unknown, string.Empty));

    private static HumanInputPolicyArtifact Failure()
        => HumanInputPolicyArtifactHash.Apply(new HumanInputPolicyArtifact(1, "failure-one", "revision-one", HumanInputPolicyKind.DeadlineDisposition, "workspace-one", "graph-one", "actor-one", null, HumanInputTerminalDisposition.Expired, string.Empty));
}
