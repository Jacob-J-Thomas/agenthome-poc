using System.Text;
using EmbodySense.Core.Application.HumanInput.Policies.Models;
using EmbodySense.Core.Common.HumanInput;
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
    public async Task Maximum_length_policy_and_revision_ids_fit_the_bounded_generation_and_restart_read()
    {
        using var workspace = new TestWorkspace();
        var paths = new WorkspacePaths(workspace.RootPath);
        var policy = HumanInputPolicyArtifactHash.Apply(new HumanInputPolicyArtifact(
            1,
            new string('a', HumanInputLimits.MaxIdentifierCharacters),
            new string('b', HumanInputLimits.MaxIdentifierCharacters),
            HumanInputPolicyKind.ResponseWindow,
            "workspace-one",
            "graph-one",
            "actor-one",
            3_600_000,
            HumanInputTerminalDisposition.Unknown,
            string.Empty));
        var options = new HumanInputPolicyFileStoreOptions { MaximumArtifacts = 1 };

        var committed = await new HumanInputPolicyFileStore(paths, options).CommitAsync(policy, 0);
        var read = await new HumanInputPolicyFileStore(paths, options).ReadAsync(policy.Reference);

        Assert.Equal(HumanInputPolicyFileStoreWriteStatus.Committed, committed.Status);
        Assert.Equal(1, committed.StoreGeneration);
        Assert.Equal(HumanInputPolicySourceReadStatus.Ready, read.Status);
        Assert.Equal(policy, read.Policy);
    }

    [Fact]
    public async Task Second_distinct_policy_advances_the_mutable_generation_and_preserves_the_immutable_catalog()
    {
        using var workspace = new TestWorkspace();
        var paths = new WorkspacePaths(workspace.RootPath);
        var timeout = Timeout();
        var failure = Failure();
        var store = new HumanInputPolicyFileStore(paths);

        var first = await store.CommitAsync(timeout, 0);
        var second = await store.CommitAsync(failure, 1);
        var restarted = new HumanInputPolicyFileStore(paths);
        var timeoutRead = await restarted.ReadAsync(timeout.Reference);
        var failureRead = await restarted.ReadAsync(failure.Reference);
        var replay = await restarted.CommitAsync(failure, 2);

        Assert.Equal(HumanInputPolicyFileStoreWriteStatus.Committed, first.Status);
        Assert.Equal(HumanInputPolicyFileStoreWriteStatus.Committed, second.Status);
        Assert.Equal(2, second.StoreGeneration);
        Assert.Equal(HumanInputPolicySourceReadStatus.Ready, timeoutRead.Status);
        Assert.Equal(timeout, timeoutRead.Policy);
        Assert.Equal(HumanInputPolicySourceReadStatus.Ready, failureRead.Status);
        Assert.Equal(failure, failureRead.Policy);
        Assert.Equal(HumanInputPolicyFileStoreWriteStatus.Replayed, replay.Status);
        Assert.Equal(2, replay.StoreGeneration);
    }

    [Fact]
    public async Task Linux_retained_no_follow_policy_source_supports_public_commit_read_and_restart_replay()
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        using var workspace = new TestWorkspace();
        var paths = new WorkspacePaths(workspace.RootPath);
        var policy = Timeout();
        var committed = await new HumanInputPolicyFileStore(paths).CommitAsync(policy, 0);
        var read = await new HumanInputPolicyFileStore(paths).ReadAsync(policy.Reference);
        var replay = await new HumanInputPolicyFileStore(paths).CommitAsync(policy, 1);

        Assert.Equal(HumanInputPolicyFileStoreWriteStatus.Committed, committed.Status);
        Assert.Equal(HumanInputPolicySourceReadStatus.Ready, read.Status);
        Assert.Equal(policy, read.Policy);
        Assert.Equal(HumanInputPolicyFileStoreWriteStatus.Replayed, replay.Status);
    }

    [Fact]
    public async Task Unix_same_reference_replacement_between_catalog_and_policy_reads_is_unavailable()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        using var workspace = new TestWorkspace();
        var paths = new WorkspacePaths(workspace.RootPath);
        var policy = Timeout();
        var store = new HumanInputPolicyFileStore(paths);
        Assert.Equal(HumanInputPolicyFileStoreWriteStatus.Committed, (await store.CommitAsync(policy, 0)).Status);

        var policyPath = Path.Combine(PolicyRoot(paths), policy.Reference + ".json");
        var displacedPath = policyPath + ".displaced";
        var replacement = HumanInputPolicyArtifactHash.Apply(policy with { ResponseWindowMilliseconds = 120_000 });
        var observer = new HumanInputPolicyFileStorePathRaceObserver(policyPath, displacedPath, HumanInputPolicyArtifactJson.Serialize(replacement), replacementOpen: 4);
        try
        {
            var read = await new HumanInputPolicyFileStore(paths, new HumanInputPolicyFileStoreOptions { PathObserver = observer }).ReadAsync(policy.Reference);

            Assert.True(observer.Replaced);
            Assert.Equal(4, observer.PolicyOpenCount);
            Assert.Equal(HumanInputPolicySourceReadStatus.Unavailable, read.Status);
            Assert.Null(read.Policy);
        }
        finally
        {
            if (observer.Replaced)
            {
                File.Delete(policyPath);
                File.Move(displacedPath, policyPath);
            }
        }
    }

    [Fact]
    public async Task Unix_target_replacement_after_the_first_proof_is_unavailable_without_generation_commit()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        using var workspace = new TestWorkspace();
        var paths = new WorkspacePaths(workspace.RootPath);
        var seed = Timeout();
        var policy = Failure();
        Assert.Equal(HumanInputPolicyFileStoreWriteStatus.Committed, (await new HumanInputPolicyFileStore(paths).CommitAsync(seed, 0)).Status);
        var root = PolicyRoot(paths);
        var generationPath = Path.Combine(root, "generation");
        var generationBefore = await File.ReadAllBytesAsync(generationPath);
        var artifactPath = Path.Combine(root, policy.Reference + ".json");
        var displacedPath = artifactPath + ".displaced";
        var replaced = false;
        var options = new HumanInputPolicyFileStoreOptions
        {
            PhysicalBoundaryObserver = (part, boundary, _) =>
            {
                if (!replaced && part == HumanInputPolicyFileStorePublicationPart.PolicyArtifact && boundary == HumanInputPolicyFileStorePhysicalPersistenceBoundary.TargetProven)
                {
                    File.Move(artifactPath, displacedPath);
                    File.WriteAllBytes(artifactPath, HumanInputPolicyArtifactJson.Serialize(HumanInputPolicyArtifactHash.Apply(policy with { AuthorityActorId = "actor-two" })));
                    replaced = true;
                }

                return ValueTask.CompletedTask;
            }
        };

        var result = await new HumanInputPolicyFileStore(paths, options).CommitAsync(policy, 1);

        Assert.True(replaced);
        Assert.Equal(HumanInputPolicyFileStoreWriteStatus.Unavailable, result.Status);
        Assert.Equal(generationBefore, await File.ReadAllBytesAsync(generationPath));
        Assert.True(File.Exists(Path.Combine(root, "publication.intent")));
        Assert.Equal(HumanInputPolicySourceReadStatus.Unavailable, (await new HumanInputPolicyFileStore(paths).ReadAsync(policy.Reference)).Status);
    }

    [Fact]
    public async Task Unix_in_place_target_overwrite_after_the_first_proof_is_unavailable_without_generation_commit()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        using var workspace = new TestWorkspace();
        var paths = new WorkspacePaths(workspace.RootPath);
        var seed = Timeout();
        var policy = Failure();
        Assert.Equal(HumanInputPolicyFileStoreWriteStatus.Committed, (await new HumanInputPolicyFileStore(paths).CommitAsync(seed, 0)).Status);
        var root = PolicyRoot(paths);
        var generationPath = Path.Combine(root, "generation");
        var generationBefore = await File.ReadAllBytesAsync(generationPath);
        var artifactPath = Path.Combine(root, policy.Reference + ".json");
        var overwritten = false;
        var options = new HumanInputPolicyFileStoreOptions
        {
            PhysicalBoundaryObserver = (part, boundary, _) =>
            {
                if (!overwritten && part == HumanInputPolicyFileStorePublicationPart.PolicyArtifact && boundary == HumanInputPolicyFileStorePhysicalPersistenceBoundary.TargetProven)
                {
                    var replacement = HumanInputPolicyArtifactJson.Serialize(HumanInputPolicyArtifactHash.Apply(policy with { AuthorityActorId = "actor-two" }));
                    using var stream = new FileStream(artifactPath, FileMode.Open, FileAccess.Write, FileShare.ReadWrite);
                    stream.SetLength(replacement.Length);
                    stream.Write(replacement);
                    stream.Flush(flushToDisk: true);
                    overwritten = true;
                }

                return ValueTask.CompletedTask;
            }
        };

        var result = await new HumanInputPolicyFileStore(paths, options).CommitAsync(policy, 1);

        Assert.True(overwritten);
        Assert.Equal(HumanInputPolicyFileStoreWriteStatus.Unavailable, result.Status);
        Assert.Equal(generationBefore, await File.ReadAllBytesAsync(generationPath));
        Assert.True(File.Exists(Path.Combine(root, "publication.intent")));
        Assert.Equal(HumanInputPolicySourceReadStatus.Unavailable, (await new HumanInputPolicyFileStore(paths).ReadAsync(policy.Reference)).Status);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Unix_mutation_lock_disappearance_or_replacement_fails_closed_before_generation_publication(bool createReplacement)
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        using var workspace = new TestWorkspace();
        var paths = new WorkspacePaths(workspace.RootPath);
        var policy = Timeout();
        var seed = new HumanInputPolicyFileStore(paths);
        Assert.Equal(HumanInputPolicyFileStoreWriteStatus.Committed, (await seed.CommitAsync(policy, 0)).Status);

        var root = PolicyRoot(paths);
        var lockPath = Path.Combine(root, "mutation.lock");
        var displacedPath = lockPath + ".displaced";
        var generationPath = Path.Combine(root, "generation");
        var generationBefore = await File.ReadAllBytesAsync(generationPath);
        var swapped = false;
        var options = new HumanInputPolicyFileStoreOptions
        {
            PhysicalBoundaryObserver = (part, boundary, _) =>
            {
                if (!swapped && part == HumanInputPolicyFileStorePublicationPart.PolicyArtifact && boundary == HumanInputPolicyFileStorePhysicalPersistenceBoundary.TargetProven)
                {
                    File.Move(lockPath, displacedPath);
                    if (createReplacement)
                    {
                        File.WriteAllText(lockPath, "replacement lock");
                    }

                    swapped = true;
                }

                return ValueTask.CompletedTask;
            }
        };

        try
        {
            var result = await new HumanInputPolicyFileStore(paths, options).CommitAsync(Failure(), 1);

            Assert.True(swapped);
            Assert.Equal(HumanInputPolicyFileStoreWriteStatus.Unavailable, result.Status);
            Assert.Equal(generationBefore, await File.ReadAllBytesAsync(generationPath));
        }
        finally
        {
            if (swapped)
            {
                if (File.Exists(lockPath))
                {
                    File.Delete(lockPath);
                }

                File.Move(displacedPath, lockPath);
            }
        }
    }

    [Fact]
    public async Task Windows_owned_mutation_lock_is_enumerated_through_its_retained_handle()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        using var workspace = new TestWorkspace();
        var paths = new WorkspacePaths(workspace.RootPath);
        var timeout = Timeout();
        var failure = Failure();
        var store = new HumanInputPolicyFileStore(paths);

        var first = await store.CommitAsync(timeout, 0);
        var second = await store.CommitAsync(failure, 1);
        var read = await new HumanInputPolicyFileStore(paths).ReadAsync(failure.Reference);

        Assert.Equal(HumanInputPolicyFileStoreWriteStatus.Committed, first.Status);
        Assert.Equal(HumanInputPolicyFileStoreWriteStatus.Committed, second.Status);
        Assert.Equal(HumanInputPolicySourceReadStatus.Ready, read.Status);
        Assert.Equal(failure, read.Policy);
    }

    [Theory]
    [InlineData("agent")]
    [InlineData("human-input")]
    [InlineData("policies")]
    public async Task Unix_linked_policy_root_components_fail_closed_without_outside_write(string linkedComponent)
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        using var workspace = new TestWorkspace();
        using var outside = new TestWorkspace();
        var paths = new WorkspacePaths(workspace.RootPath);
        var linkedPath = linkedComponent switch
        {
            "agent" => paths.AgentPath,
            "human-input" => Path.Combine(paths.AgentPath, "human-input"),
            "policies" => Path.Combine(paths.AgentPath, "human-input", "policies"),
            _ => throw new ArgumentOutOfRangeException(nameof(linkedComponent))
        };
        var parent = Path.GetDirectoryName(linkedPath)!;
        Directory.CreateDirectory(parent);
        Directory.CreateSymbolicLink(linkedPath, outside.RootPath);

        var result = await new HumanInputPolicyFileStore(paths).CommitAsync(Timeout(), 0);

        Assert.Equal(HumanInputPolicyFileStoreWriteStatus.Unavailable, result.Status);
        Assert.Empty(Directory.EnumerateFileSystemEntries(outside.RootPath, "*", SearchOption.AllDirectories));
    }

    [Fact]
    public async Task Windows_reparse_human_input_component_fails_closed_when_the_host_allows_link_creation()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        using var workspace = new TestWorkspace();
        using var outside = new TestWorkspace();
        var paths = new WorkspacePaths(workspace.RootPath);
        Directory.CreateDirectory(paths.AgentPath);
        try
        {
            Directory.CreateSymbolicLink(Path.Combine(paths.AgentPath, "human-input"), outside.RootPath);
        }
        catch (Exception exception) when (exception is UnauthorizedAccessException or IOException or PlatformNotSupportedException)
        {
            return;
        }

        var result = await new HumanInputPolicyFileStore(paths).CommitAsync(Timeout(), 0);

        Assert.Equal(HumanInputPolicyFileStoreWriteStatus.Unavailable, result.Status);
        Assert.Empty(Directory.EnumerateFileSystemEntries(outside.RootPath, "*", SearchOption.AllDirectories));
    }

    [Fact]
    public async Task Retained_policy_parent_revalidation_rejects_an_ancestor_link_swap_without_outside_write()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        using var workspace = new TestWorkspace();
        using var outside = new TestWorkspace();
        var paths = new WorkspacePaths(workspace.RootPath);
        var source = Path.Combine(paths.AgentPath, "human-input");
        var retained = Path.Combine(paths.AgentPath, "retained-human-input");
        var swapped = false;
        var options = new HumanInputPolicyFileStoreOptions
        {
            PhysicalBoundaryObserver = (part, boundary, _) =>
            {
                if (!swapped && part == HumanInputPolicyFileStorePublicationPart.PublicationIntent && boundary == HumanInputPolicyFileStorePhysicalPersistenceBoundary.CanonicalRenamed)
                {
                    Directory.Move(source, retained);
                    Directory.CreateSymbolicLink(source, outside.RootPath);
                    swapped = true;
                }
                return ValueTask.CompletedTask;
            }
        };

        try
        {
            var result = await new HumanInputPolicyFileStore(paths, options).CommitAsync(Timeout(), 0);

            Assert.True(swapped);
            Assert.Equal(HumanInputPolicyFileStoreWriteStatus.Unavailable, result.Status);
            Assert.Empty(Directory.EnumerateFileSystemEntries(outside.RootPath, "*", SearchOption.AllDirectories));
        }
        finally
        {
            if (swapped)
            {
                Directory.Delete(source);
                Directory.Move(retained, source);
            }
        }
    }

    [Fact]
    public async Task Windows_exact_retry_repairs_only_the_proved_artifact_visible_intent_absent_orphan()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        using var workspace = new TestWorkspace();
        var paths = new WorkspacePaths(workspace.RootPath);
        var timeout = Timeout();
        var failure = Failure();
        Assert.Equal(HumanInputPolicyFileStoreWriteStatus.Committed, (await new HumanInputPolicyFileStore(paths).CommitAsync(timeout, 0)).Status);
        await File.WriteAllBytesAsync(Path.Combine(PolicyRoot(paths), failure.Reference + ".json"), HumanInputPolicyArtifactJson.Serialize(failure));

        var interruptedRead = await new HumanInputPolicyFileStore(paths).ReadAsync(failure.Reference);
        var replay = await new HumanInputPolicyFileStore(paths).CommitAsync(failure, 1);
        var recovered = await new HumanInputPolicyFileStore(paths).ReadAsync(failure.Reference);

        Assert.Equal(HumanInputPolicySourceReadStatus.Unavailable, interruptedRead.Status);
        Assert.Equal(HumanInputPolicyFileStoreWriteStatus.Replayed, replay.Status);
        Assert.Equal(2, replay.StoreGeneration);
        Assert.Equal(HumanInputPolicySourceReadStatus.Ready, recovered.Status);
        Assert.Equal(failure, recovered.Policy);
    }

    [Fact]
    public async Task Windows_divergent_orphan_cannot_be_repaired_by_an_already_cataloged_exact_retry()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        using var workspace = new TestWorkspace();
        var paths = new WorkspacePaths(workspace.RootPath);
        var timeout = Timeout();
        var failure = Failure();
        Assert.Equal(HumanInputPolicyFileStoreWriteStatus.Committed, (await new HumanInputPolicyFileStore(paths).CommitAsync(timeout, 0)).Status);
        await File.WriteAllBytesAsync(Path.Combine(PolicyRoot(paths), failure.Reference + ".json"), HumanInputPolicyArtifactJson.Serialize(failure));

        var divergent = await new HumanInputPolicyFileStore(paths).CommitAsync(timeout, 1);
        var recovered = await new HumanInputPolicyFileStore(paths).CommitAsync(failure, 1);

        Assert.Equal(HumanInputPolicyFileStoreWriteStatus.Unavailable, divergent.Status);
        Assert.Equal(HumanInputPolicyFileStoreWriteStatus.Replayed, recovered.Status);
        Assert.Equal(2, recovered.StoreGeneration);
    }

    [Theory]
    [InlineData(HumanInputPolicyFileStorePublicationBoundary.IntentPublished, HumanInputPolicySourceReadStatus.Unavailable)]
    [InlineData(HumanInputPolicyFileStorePublicationBoundary.ArtifactPublished, HumanInputPolicySourceReadStatus.Unavailable)]
    [InlineData(HumanInputPolicyFileStorePublicationBoundary.GenerationPublished, HumanInputPolicySourceReadStatus.Unavailable)]
    [InlineData(HumanInputPolicyFileStorePublicationBoundary.PublicationIntentRetired, HumanInputPolicySourceReadStatus.Ready)]
    public async Task Process_loss_after_each_durable_publication_boundary_is_recovered_only_by_an_exact_restart_retry(HumanInputPolicyFileStorePublicationBoundary boundary, HumanInputPolicySourceReadStatus expectedInterruptedReadStatus)
    {
        using var workspace = new TestWorkspace();
        var paths = new WorkspacePaths(workspace.RootPath);
        Assert.Equal(HumanInputPolicyFileStoreWriteStatus.Committed, (await new HumanInputPolicyFileStore(paths).CommitAsync(Timeout(), 0)).Status);
        var policy = Failure();
        var interrupted = false;
        var options = new HumanInputPolicyFileStoreOptions
        {
            DurableBoundaryObserver = observed =>
            {
                if (!interrupted && observed == boundary)
                {
                    interrupted = true;
                    throw new IOException("Simulated process loss after a durable policy-publication boundary.");
                }
            }
        };

        var first = await new HumanInputPolicyFileStore(paths, options).CommitAsync(policy, 1);
        var unavailableRead = await new HumanInputPolicyFileStore(paths).ReadAsync(policy.Reference);
        var recovered = await new HumanInputPolicyFileStore(paths).CommitAsync(policy, 1);
        var read = await new HumanInputPolicyFileStore(paths).ReadAsync(policy.Reference);

        Assert.True(interrupted);
        Assert.Equal(HumanInputPolicyFileStoreWriteStatus.Unavailable, first.Status);
        Assert.Equal(expectedInterruptedReadStatus, unavailableRead.Status);
        Assert.Equal(HumanInputPolicyFileStoreWriteStatus.Replayed, recovered.Status);
        Assert.Equal(2, recovered.StoreGeneration);
        Assert.Equal(HumanInputPolicySourceReadStatus.Ready, read.Status);
        Assert.Equal(policy, read.Policy);
    }

    [Theory]
    [InlineData(HumanInputPolicyFileStorePublicationBoundary.IntentPublished)]
    [InlineData(HumanInputPolicyFileStorePublicationBoundary.ArtifactPublished)]
    [InlineData(HumanInputPolicyFileStorePublicationBoundary.GenerationPublished)]
    [InlineData(HumanInputPolicyFileStorePublicationBoundary.PublicationIntentRetired)]
    public async Task Cancellation_after_each_durable_publication_boundary_is_recovered_only_by_an_exact_restart_retry(HumanInputPolicyFileStorePublicationBoundary boundary)
    {
        using var workspace = new TestWorkspace();
        using var cancellation = new CancellationTokenSource();
        var paths = new WorkspacePaths(workspace.RootPath);
        Assert.Equal(HumanInputPolicyFileStoreWriteStatus.Committed, (await new HumanInputPolicyFileStore(paths).CommitAsync(Timeout(), 0)).Status);
        var policy = Failure();
        var interrupted = false;
        var options = new HumanInputPolicyFileStoreOptions
        {
            DurableBoundaryObserver = observed =>
            {
                if (!interrupted && observed == boundary)
                {
                    interrupted = true;
                    cancellation.Cancel();
                }
            }
        };

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new HumanInputPolicyFileStore(paths, options).CommitAsync(policy, 1, cancellation.Token));
        var recovered = await new HumanInputPolicyFileStore(paths).CommitAsync(policy, 1);

        Assert.True(interrupted);
        Assert.Equal(HumanInputPolicyFileStoreWriteStatus.Replayed, recovered.Status);
        Assert.Equal(2, recovered.StoreGeneration);
        Assert.Equal(HumanInputPolicySourceReadStatus.Ready, (await new HumanInputPolicyFileStore(paths).ReadAsync(policy.Reference)).Status);
    }

    [Theory]
    [InlineData(HumanInputPolicyFileStorePublicationPart.PublicationIntent, HumanInputPolicyFileStorePhysicalPersistenceBoundary.StagedFileFlushed, HumanInputPolicyFileStoreWriteStatus.Committed)]
    [InlineData(HumanInputPolicyFileStorePublicationPart.PublicationIntent, HumanInputPolicyFileStorePhysicalPersistenceBoundary.CanonicalRenamed, HumanInputPolicyFileStoreWriteStatus.Replayed)]
    [InlineData(HumanInputPolicyFileStorePublicationPart.PublicationIntent, HumanInputPolicyFileStorePhysicalPersistenceBoundary.ParentDirectoryFlushed, HumanInputPolicyFileStoreWriteStatus.Replayed)]
    [InlineData(HumanInputPolicyFileStorePublicationPart.PublicationIntent, HumanInputPolicyFileStorePhysicalPersistenceBoundary.TargetProven, HumanInputPolicyFileStoreWriteStatus.Replayed)]
    [InlineData(HumanInputPolicyFileStorePublicationPart.PolicyArtifact, HumanInputPolicyFileStorePhysicalPersistenceBoundary.StagedFileFlushed, HumanInputPolicyFileStoreWriteStatus.Replayed)]
    [InlineData(HumanInputPolicyFileStorePublicationPart.PolicyArtifact, HumanInputPolicyFileStorePhysicalPersistenceBoundary.CanonicalRenamed, HumanInputPolicyFileStoreWriteStatus.Replayed)]
    [InlineData(HumanInputPolicyFileStorePublicationPart.PolicyArtifact, HumanInputPolicyFileStorePhysicalPersistenceBoundary.ParentDirectoryFlushed, HumanInputPolicyFileStoreWriteStatus.Replayed)]
    [InlineData(HumanInputPolicyFileStorePublicationPart.PolicyArtifact, HumanInputPolicyFileStorePhysicalPersistenceBoundary.TargetProven, HumanInputPolicyFileStoreWriteStatus.Replayed)]
    [InlineData(HumanInputPolicyFileStorePublicationPart.Generation, HumanInputPolicyFileStorePhysicalPersistenceBoundary.StagedFileFlushed, HumanInputPolicyFileStoreWriteStatus.Replayed)]
    [InlineData(HumanInputPolicyFileStorePublicationPart.Generation, HumanInputPolicyFileStorePhysicalPersistenceBoundary.CanonicalRenamed, HumanInputPolicyFileStoreWriteStatus.Replayed)]
    [InlineData(HumanInputPolicyFileStorePublicationPart.Generation, HumanInputPolicyFileStorePhysicalPersistenceBoundary.ParentDirectoryFlushed, HumanInputPolicyFileStoreWriteStatus.Replayed)]
    [InlineData(HumanInputPolicyFileStorePublicationPart.Generation, HumanInputPolicyFileStorePhysicalPersistenceBoundary.TargetProven, HumanInputPolicyFileStoreWriteStatus.Replayed)]
    [InlineData(HumanInputPolicyFileStorePublicationPart.PublicationIntent, HumanInputPolicyFileStorePhysicalPersistenceBoundary.Deleted, HumanInputPolicyFileStoreWriteStatus.Replayed)]
    [InlineData(HumanInputPolicyFileStorePublicationPart.PublicationIntent, HumanInputPolicyFileStorePhysicalPersistenceBoundary.Retired, HumanInputPolicyFileStoreWriteStatus.Replayed)]
    public async Task Every_physical_publication_and_retirement_boundary_preserves_an_exact_restart_outcome(HumanInputPolicyFileStorePublicationPart part, HumanInputPolicyFileStorePhysicalPersistenceBoundary boundary, HumanInputPolicyFileStoreWriteStatus expectedRetryStatus)
    {
        using var workspace = new TestWorkspace();
        var paths = new WorkspacePaths(workspace.RootPath);
        Assert.Equal(HumanInputPolicyFileStoreWriteStatus.Committed, (await new HumanInputPolicyFileStore(paths).CommitAsync(Timeout(), 0)).Status);
        var policy = Failure();
        var interrupted = false;
        var options = new HumanInputPolicyFileStoreOptions
        {
            PhysicalBoundaryObserver = (observedPart, observedBoundary, _) =>
            {
                if (!interrupted && observedPart == part && observedBoundary == boundary)
                {
                    interrupted = true;
                    return ValueTask.FromException(new IOException("Simulated power loss inside the retained-parent policy publication protocol."));
                }

                return ValueTask.CompletedTask;
            }
        };

        var first = await new HumanInputPolicyFileStore(paths, options).CommitAsync(policy, 1);
        var retry = await new HumanInputPolicyFileStore(paths).CommitAsync(policy, 1);
        var read = await new HumanInputPolicyFileStore(paths).ReadAsync(policy.Reference);

        Assert.True(interrupted);
        Assert.Equal(HumanInputPolicyFileStoreWriteStatus.Unavailable, first.Status);
        Assert.Equal(expectedRetryStatus, retry.Status);
        Assert.Equal(2, retry.StoreGeneration);
        Assert.Equal(HumanInputPolicySourceReadStatus.Ready, read.Status);
        Assert.Equal(policy, read.Policy);
    }

    [Fact]
    public async Task Exact_derived_interrupted_temporary_artifact_is_retired_by_a_restart_safe_read()
    {
        using var workspace = new TestWorkspace();
        var paths = new WorkspacePaths(workspace.RootPath);
        var policy = Timeout();
        Assert.Equal(HumanInputPolicyFileStoreWriteStatus.Committed, (await new HumanInputPolicyFileStore(paths).CommitAsync(policy, 0)).Status);
        var temporaryPath = Path.Combine(PolicyRoot(paths), "generation.tmp-" + new string('a', 32));
        await File.WriteAllTextAsync(temporaryPath, "sensitive interrupted generation bytes");

        var read = await new HumanInputPolicyFileStore(paths).ReadAsync(policy.Reference);

        Assert.Equal(HumanInputPolicySourceReadStatus.Ready, read.Status);
        Assert.Equal(policy, read.Policy);
        Assert.False(File.Exists(temporaryPath));
    }

    [Theory]
    [InlineData(HumanInputPolicyFileStorePhysicalPersistenceBoundary.Deleted)]
    [InlineData(HumanInputPolicyFileStorePhysicalPersistenceBoundary.Retired)]
    public async Task Interrupted_temporary_retirement_boundaries_recover_on_the_next_restart_safe_read(HumanInputPolicyFileStorePhysicalPersistenceBoundary boundary)
    {
        using var workspace = new TestWorkspace();
        var paths = new WorkspacePaths(workspace.RootPath);
        var policy = Timeout();
        Assert.Equal(HumanInputPolicyFileStoreWriteStatus.Committed, (await new HumanInputPolicyFileStore(paths).CommitAsync(policy, 0)).Status);
        var temporaryPath = Path.Combine(PolicyRoot(paths), "generation.tmp-" + new string('b', 32));
        await File.WriteAllTextAsync(temporaryPath, "sensitive interrupted generation bytes");
        var interrupted = false;
        var options = new HumanInputPolicyFileStoreOptions
        {
            PhysicalBoundaryObserver = (part, observedBoundary, _) =>
            {
                if (!interrupted && part == HumanInputPolicyFileStorePublicationPart.InterruptedTemporary && observedBoundary == boundary)
                {
                    interrupted = true;
                    return ValueTask.FromException(new IOException("Simulated power loss inside temporary retirement."));
                }

                return ValueTask.CompletedTask;
            }
        };

        var interruptedRead = await new HumanInputPolicyFileStore(paths, options).ReadAsync(policy.Reference);
        var recoveredRead = await new HumanInputPolicyFileStore(paths).ReadAsync(policy.Reference);

        Assert.True(interrupted);
        Assert.Equal(HumanInputPolicySourceReadStatus.Unavailable, interruptedRead.Status);
        Assert.Equal(HumanInputPolicySourceReadStatus.Ready, recoveredRead.Status);
        Assert.Equal(policy, recoveredRead.Policy);
        Assert.False(File.Exists(temporaryPath));
    }

    [Fact]
    public async Task Repeated_single_interrupted_temporary_artifacts_are_retired_without_catalog_growth()
    {
        using var workspace = new TestWorkspace();
        var paths = new WorkspacePaths(workspace.RootPath);
        var policy = Timeout();
        Assert.Equal(HumanInputPolicyFileStoreWriteStatus.Committed, (await new HumanInputPolicyFileStore(paths).CommitAsync(policy, 0)).Status);

        for (var value = 0; value < 4; value++)
        {
            var temporaryPath = Path.Combine(PolicyRoot(paths), "generation.tmp-" + value.ToString("x32", System.Globalization.CultureInfo.InvariantCulture));
            await File.WriteAllTextAsync(temporaryPath, "sensitive interrupted generation bytes");
            var read = await new HumanInputPolicyFileStore(paths).ReadAsync(policy.Reference);

            Assert.Equal(HumanInputPolicySourceReadStatus.Ready, read.Status);
            Assert.False(File.Exists(temporaryPath));
            Assert.Equal(3, Directory.EnumerateFileSystemEntries(PolicyRoot(paths), "*", SearchOption.TopDirectoryOnly).Count());
        }
    }

    [Fact]
    public async Task Multiple_lookalike_or_arbitrary_interrupted_artifacts_fail_closed_without_retiring_unrecognized_bytes()
    {
        using var workspace = new TestWorkspace();
        var paths = new WorkspacePaths(workspace.RootPath);
        var policy = Timeout();
        Assert.Equal(HumanInputPolicyFileStoreWriteStatus.Committed, (await new HumanInputPolicyFileStore(paths).CommitAsync(policy, 0)).Status);
        var root = PolicyRoot(paths);
        var first = Path.Combine(root, "generation.tmp-" + new string('c', 32));
        var second = Path.Combine(root, "publication.intent.tmp-" + new string('d', 32));
        await File.WriteAllTextAsync(first, "first sensitive interrupted bytes");
        await File.WriteAllTextAsync(second, "second sensitive interrupted bytes");

        var bounded = await new HumanInputPolicyFileStore(paths).ReadAsync(policy.Reference);

        Assert.Equal(HumanInputPolicySourceReadStatus.Unavailable, bounded.Status);
        Assert.True(File.Exists(first));
        Assert.True(File.Exists(second));

        File.Delete(second);
        var lookalike = Path.Combine(root, "generation.tmp-" + new string('A', 32));
        await File.WriteAllTextAsync(lookalike, "lookalike sensitive bytes");
        var hostile = await new HumanInputPolicyFileStore(paths).ReadAsync(policy.Reference);

        Assert.Equal(HumanInputPolicySourceReadStatus.Unavailable, hostile.Status);
        Assert.True(File.Exists(first));
        Assert.True(File.Exists(lookalike));

        File.Delete(first);
        File.Delete(lookalike);
        var arbitrary = Path.Combine(root, "retained-sensitive.bin");
        await File.WriteAllTextAsync(arbitrary, "arbitrary sensitive bytes");
        var unsupported = await new HumanInputPolicyFileStore(paths).ReadAsync(policy.Reference);

        Assert.Equal(HumanInputPolicySourceReadStatus.Unavailable, unsupported.Status);
        Assert.True(File.Exists(arbitrary));
    }

    [Fact]
    public async Task Corrupt_exact_temporary_candidate_is_rejected_without_recursive_or_silent_cleanup()
    {
        using var workspace = new TestWorkspace();
        var paths = new WorkspacePaths(workspace.RootPath);
        var policy = Timeout();
        Assert.Equal(HumanInputPolicyFileStoreWriteStatus.Committed, (await new HumanInputPolicyFileStore(paths).CommitAsync(policy, 0)).Status);
        var temporaryPath = Path.Combine(PolicyRoot(paths), "generation.tmp-" + new string('e', 32));
        Directory.CreateDirectory(temporaryPath);

        var result = await new HumanInputPolicyFileStore(paths).ReadAsync(policy.Reference);

        Assert.Equal(HumanInputPolicySourceReadStatus.Unavailable, result.Status);
        Assert.True(Directory.Exists(temporaryPath));
    }

    [Fact]
    public async Task Divergent_exact_retry_cannot_replace_an_interrupted_immutable_artifact()
    {
        using var workspace = new TestWorkspace();
        var paths = new WorkspacePaths(workspace.RootPath);
        var policy = Timeout();
        var divergent = HumanInputPolicyArtifactHash.Apply(policy with { ResponseWindowMilliseconds = 120_000 });
        var interrupted = false;
        var options = new HumanInputPolicyFileStoreOptions
        {
            DurableBoundaryObserver = observed =>
            {
                if (!interrupted && observed == HumanInputPolicyFileStorePublicationBoundary.ArtifactPublished)
                {
                    interrupted = true;
                    throw new IOException("Simulated process loss after the immutable artifact publication.");
                }
            }
        };

        var first = await new HumanInputPolicyFileStore(paths, options).CommitAsync(policy, 0);
        var divergentRetry = await new HumanInputPolicyFileStore(paths).CommitAsync(divergent, 0);
        var recovered = await new HumanInputPolicyFileStore(paths).CommitAsync(policy, 0);

        Assert.True(interrupted);
        Assert.Equal(HumanInputPolicyFileStoreWriteStatus.Unavailable, first.Status);
        Assert.Equal(HumanInputPolicyFileStoreWriteStatus.Unavailable, divergentRetry.Status);
        Assert.Equal(HumanInputPolicyFileStoreWriteStatus.Replayed, recovered.Status);
        Assert.Equal(HumanInputPolicySourceReadStatus.Ready, (await new HumanInputPolicyFileStore(paths).ReadAsync(policy.Reference)).Status);
    }

    [Fact]
    public async Task Corrupt_interrupted_evidence_is_not_silently_repaired()
    {
        using var workspace = new TestWorkspace();
        var paths = new WorkspacePaths(workspace.RootPath);
        var policy = Timeout();
        var interrupted = false;
        var options = new HumanInputPolicyFileStoreOptions
        {
            DurableBoundaryObserver = observed =>
            {
                if (!interrupted && observed == HumanInputPolicyFileStorePublicationBoundary.ArtifactPublished)
                {
                    interrupted = true;
                    throw new IOException("Simulated process loss after the immutable artifact publication.");
                }
            }
        };

        var first = await new HumanInputPolicyFileStore(paths, options).CommitAsync(policy, 0);
        var path = Path.Combine(paths.AgentPath, "human-input", "policies", "timeout-one@revision-one.json");
        await File.WriteAllTextAsync(path, "{\"unsupported\":true}");
        var retry = await new HumanInputPolicyFileStore(paths).CommitAsync(policy, 0);
        var stored = await File.ReadAllTextAsync(path);

        Assert.True(interrupted);
        Assert.Equal(HumanInputPolicyFileStoreWriteStatus.Unavailable, first.Status);
        Assert.Equal(HumanInputPolicyFileStoreWriteStatus.Unavailable, retry.Status);
        Assert.Equal("{\"unsupported\":true}", stored);
        Assert.Equal(HumanInputPolicySourceReadStatus.Unavailable, (await new HumanInputPolicyFileStore(paths).ReadAsync(policy.Reference)).Status);
    }

    [Fact]
    public async Task Corrupt_interrupted_publication_intent_is_not_silently_repaired()
    {
        using var workspace = new TestWorkspace();
        var paths = new WorkspacePaths(workspace.RootPath);
        var policy = Timeout();
        var interrupted = false;
        var options = new HumanInputPolicyFileStoreOptions
        {
            DurableBoundaryObserver = observed =>
            {
                if (!interrupted && observed == HumanInputPolicyFileStorePublicationBoundary.IntentPublished)
                {
                    interrupted = true;
                    throw new IOException("Simulated process loss after the publication intent.");
                }
            }
        };

        var first = await new HumanInputPolicyFileStore(paths, options).CommitAsync(policy, 0);
        var intentPath = Path.Combine(paths.AgentPath, "human-input", "policies", "publication.intent");
        await File.WriteAllTextAsync(intentPath, "{\"unsupported\":true}");
        var retry = await new HumanInputPolicyFileStore(paths).CommitAsync(policy, 0);

        Assert.True(interrupted);
        Assert.Equal(HumanInputPolicyFileStoreWriteStatus.Unavailable, first.Status);
        Assert.Equal(HumanInputPolicyFileStoreWriteStatus.Unavailable, retry.Status);
        Assert.Equal("{\"unsupported\":true}", await File.ReadAllTextAsync(intentPath));
        Assert.False(File.Exists(Path.Combine(paths.AgentPath, "human-input", "policies", "timeout-one@revision-one.json")));
        Assert.False(File.Exists(Path.Combine(paths.AgentPath, "human-input", "policies", "generation")));
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
    public async Task Semantically_equivalent_noncanonical_persisted_policy_bytes_are_unavailable()
    {
        using var workspace = new TestWorkspace();
        var paths = new WorkspacePaths(workspace.RootPath);
        var policy = Timeout();
        var store = new HumanInputPolicyFileStore(paths);
        Assert.Equal(HumanInputPolicyFileStoreWriteStatus.Committed, (await store.CommitAsync(policy, 0)).Status);

        var path = Path.Combine(paths.AgentPath, "human-input", "policies", "timeout-one@revision-one.json");
        await File.WriteAllBytesAsync(path, Encoding.UTF8.GetBytes(" " + Encoding.UTF8.GetString(HumanInputPolicyArtifactJson.Serialize(policy))));

        var result = await new HumanInputPolicyFileStore(paths).ReadAsync(policy.Reference);

        Assert.Equal(HumanInputPolicySourceReadStatus.Unavailable, result.Status);
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

    private static string PolicyRoot(WorkspacePaths paths) => Path.Combine(paths.AgentPath, "human-input", "policies");
}
