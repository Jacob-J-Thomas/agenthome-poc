using System.Text;
using EmbodySense.Core.Application.Inference.Profiles.Models;
using EmbodySense.Core.Common.Inference.Profiles.Models;
using EmbodySense.Core.Persistence.Inference.Profiles;
using EmbodySense.Core.Persistence.Inference.Profiles.Models;
using EmbodySense.Tests.Support;

namespace EmbodySense.Core.Persistence.Tests.Inference.Profiles;

public sealed class ModelProfileMetadataStoreTests
{
    [Fact]
    public async Task Publish_restart_read_and_exact_operation_replay_retain_one_authenticated_revision()
    {
        using var workspace = new TestWorkspace();
        var metadata = ModelProfilePersistenceTestData.Metadata();
        var first = await Store(workspace).PublishAsync("seed-one", metadata, null);
        var restarted = Store(workspace);
        var read = await restarted.ReadAsync(metadata.DescriptorIdentity.Id);
        var replay = await restarted.PublishAsync("seed-one", metadata, null);

        Assert.Equal(ModelProfileMetadataPublishStatus.Published, first.Status);
        Assert.NotNull(first.SourceRevisionHash);
        Assert.Equal(ModelProfileSourceReadStatus.Found, read.Status);
        Assert.Equal(first.SourceRevisionHash, read.SourceRevisionHash);
        Assert.Equal(metadata.ContentHash, read.Metadata?.ContentHash);
        Assert.NotSame(metadata, read.Metadata);
        Assert.Equal(ModelProfileMetadataPublishStatus.AlreadyPresent, replay.Status);
        Assert.Equal(first.SourceRevisionHash, replay.SourceRevisionHash);
        Assert.True(File.Exists(PrimaryPath(workspace)));
        Assert.True(File.Exists(ProofPath(workspace)));
        Assert.False((await File.ReadAllTextAsync(PrimaryPath(workspace))).StartsWith('\ufeff'));
    }

    [Fact]
    public async Task Same_content_new_operation_is_durably_bound_and_later_reuse_with_changed_metadata_conflicts_after_restart()
    {
        using var workspace = new TestWorkspace();
        var metadata = ModelProfilePersistenceTestData.Metadata();
        var store = Store(workspace);
        var first = await store.PublishAsync("seed-one", metadata, null);
        var acceptedNoOp = await store.PublishAsync("seed-no-op", metadata, first.SourceRevisionHash);
        var afterAccepted = await File.ReadAllTextAsync(PrimaryPath(workspace));

        var hostileReuse = await Store(workspace).PublishAsync(
            "seed-no-op",
            ModelProfilePersistenceTestData.Metadata(configurationRevision: 2, configurationHash: 'b'),
            first.SourceRevisionHash);
        var exactReplay = await Store(workspace).PublishAsync("seed-no-op", metadata, first.SourceRevisionHash);

        Assert.Equal(ModelProfileMetadataPublishStatus.Published, first.Status);
        Assert.Equal(ModelProfileMetadataPublishStatus.AlreadyPresent, acceptedNoOp.Status);
        Assert.Equal(first.SourceRevisionHash, acceptedNoOp.SourceRevisionHash);
        Assert.Equal(ModelProfileMetadataPublishStatus.Conflict, hostileReuse.Status);
        Assert.Equal(ModelProfileMetadataPublishStatus.AlreadyPresent, exactReplay.Status);
        Assert.Equal(afterAccepted, await File.ReadAllTextAsync(PrimaryPath(workspace)));
        Assert.Equal(1, Count(afterAccepted, "\"operationId\": \"seed-no-op\""));
    }

    [Fact]
    public async Task Changed_metadata_requires_exact_current_revision_and_strict_configuration_advance()
    {
        using var workspace = new TestWorkspace();
        var store = Store(workspace);
        var initial = ModelProfilePersistenceTestData.Metadata();
        var first = await store.PublishAsync("seed-one", initial, null);
        var changed = ModelProfilePersistenceTestData.Metadata(configurationRevision: 2, configurationHash: 'b');
        var second = await store.PublishAsync("seed-two", changed, first.SourceRevisionHash);

        var stale = await store.PublishAsync("seed-three", ModelProfilePersistenceTestData.Metadata(configurationRevision: 3, configurationHash: 'c'), first.SourceRevisionHash);
        var nonAdvancing = await store.PublishAsync("seed-four", ModelProfilePersistenceTestData.Metadata(configurationRevision: 2, configurationHash: 'd'), second.SourceRevisionHash);
        var operationConflict = await store.PublishAsync("seed-two", ModelProfilePersistenceTestData.Metadata(configurationRevision: 3, configurationHash: 'e'), second.SourceRevisionHash);
        var read = await Store(workspace).ReadAsync(changed.DescriptorIdentity.Id);

        Assert.Equal(ModelProfileMetadataPublishStatus.Published, second.Status);
        Assert.NotEqual(first.SourceRevisionHash, second.SourceRevisionHash);
        Assert.Equal(ModelProfileMetadataPublishStatus.Conflict, stale.Status);
        Assert.Equal(second.SourceRevisionHash, stale.SourceRevisionHash);
        Assert.Equal(ModelProfileMetadataPublishStatus.Conflict, nonAdvancing.Status);
        Assert.Equal(ModelProfileMetadataPublishStatus.Conflict, operationConflict.Status);
        Assert.Equal(changed.ContentHash, read.Metadata?.ContentHash);
        Assert.Equal(second.SourceRevisionHash, read.SourceRevisionHash);
    }

    [Fact]
    public async Task Concurrent_compare_exchange_publishers_commit_at_most_one_changed_revision()
    {
        using var workspace = new TestWorkspace();
        var initial = await Store(workspace).PublishAsync("seed-one", ModelProfilePersistenceTestData.Metadata(), null);

        var results = await Task.WhenAll(
            Store(workspace).PublishAsync("seed-two-a", ModelProfilePersistenceTestData.Metadata(configurationRevision: 2, configurationHash: 'b'), initial.SourceRevisionHash),
            Store(workspace).PublishAsync("seed-two-b", ModelProfilePersistenceTestData.Metadata(configurationRevision: 2, configurationHash: 'c'), initial.SourceRevisionHash));

        Assert.Single(results, result => result.Status == ModelProfileMetadataPublishStatus.Published);
        Assert.Single(results, result => result.Status == ModelProfileMetadataPublishStatus.Conflict);
        var read = await Store(workspace).ReadAsync(ModelProfilePersistenceTestData.ProfileId());
        Assert.Equal(results.Single(result => result.Status == ModelProfileMetadataPublishStatus.Published).SourceRevisionHash, read.SourceRevisionHash);
    }

    [Fact]
    public async Task Count_and_byte_quotas_fail_closed_without_rewriting_current_metadata()
    {
        using var revisionWorkspace = new TestWorkspace();
        var revisionStore = Store(revisionWorkspace, new ModelProfileMetadataStoreOptions { MaxRevisions = 1 });
        var first = await revisionStore.PublishAsync("seed-one", ModelProfilePersistenceTestData.Metadata(), null);
        var receiptExhausted = await revisionStore.PublishAsync("seed-no-op", ModelProfilePersistenceTestData.Metadata(), first.SourceRevisionHash);
        var revisionExhausted = await revisionStore.PublishAsync("seed-two", ModelProfilePersistenceTestData.Metadata(configurationRevision: 2, configurationHash: 'b'), first.SourceRevisionHash);
        Assert.Equal(ModelProfileMetadataPublishStatus.Unavailable, receiptExhausted.Status);
        Assert.Equal(ModelProfileMetadataPublishStatus.Unavailable, revisionExhausted.Status);
        Assert.Equal(first.SourceRevisionHash, (await revisionStore.ReadAsync(ModelProfilePersistenceTestData.ProfileId())).SourceRevisionHash);

        using var profileWorkspace = new TestWorkspace();
        var profileStore = Store(profileWorkspace, new ModelProfileMetadataStoreOptions { MaxProfiles = 1 });
        Assert.Equal(ModelProfileMetadataPublishStatus.Published, (await profileStore.PublishAsync("seed-a", ModelProfilePersistenceTestData.Metadata(), null)).Status);
        Assert.Equal(ModelProfileMetadataPublishStatus.Unavailable, (await profileStore.PublishAsync("seed-b", ModelProfilePersistenceTestData.Metadata("org.example/model-b"), null)).Status);

        using var byteWorkspace = new TestWorkspace();
        var byteStore = Store(byteWorkspace, new ModelProfileMetadataStoreOptions { MaxArtifactUtf8Bytes = 1 });
        Assert.Equal(ModelProfileMetadataPublishStatus.Unavailable, (await byteStore.PublishAsync("seed-one", ModelProfilePersistenceTestData.Metadata(), null)).Status);
        Assert.False(File.Exists(PrimaryPath(byteWorkspace)));
    }

    [Fact]
    public async Task Reopening_authenticated_history_under_lower_instance_limits_fails_closed_without_rewriting_it()
    {
        using var revisionWorkspace = new TestWorkspace();
        var revisionStore = Store(revisionWorkspace);
        var first = await revisionStore.PublishAsync("seed-one", ModelProfilePersistenceTestData.Metadata(), null);
        Assert.Equal(ModelProfileMetadataPublishStatus.Published, first.Status);
        Assert.Equal(ModelProfileMetadataPublishStatus.Published, (await revisionStore.PublishAsync("seed-two", ModelProfilePersistenceTestData.Metadata(configurationRevision: 2, configurationHash: 'b'), first.SourceRevisionHash)).Status);
        var revisionContent = await File.ReadAllTextAsync(PrimaryPath(revisionWorkspace));
        var revisionLimited = Store(revisionWorkspace, new ModelProfileMetadataStoreOptions { MaxRevisions = 1 });

        Assert.Equal(ModelProfileSourceReadStatus.Unavailable, (await revisionLimited.ReadAsync(ModelProfilePersistenceTestData.ProfileId())).Status);
        Assert.Equal(ModelProfileMetadataPublishStatus.Unavailable, (await revisionLimited.PublishAsync("seed-three", ModelProfilePersistenceTestData.Metadata(configurationRevision: 3, configurationHash: 'c'), first.SourceRevisionHash)).Status);
        Assert.Equal(revisionContent, await File.ReadAllTextAsync(PrimaryPath(revisionWorkspace)));

        using var profileWorkspace = new TestWorkspace();
        var profileStore = Store(profileWorkspace);
        Assert.Equal(ModelProfileMetadataPublishStatus.Published, (await profileStore.PublishAsync("seed-a", ModelProfilePersistenceTestData.Metadata(), null)).Status);
        var secondProfile = ModelProfilePersistenceTestData.Metadata("org.example/model-b");
        Assert.Equal(ModelProfileMetadataPublishStatus.Published, (await profileStore.PublishAsync("seed-b", secondProfile, null)).Status);
        var profileContent = await File.ReadAllTextAsync(PrimaryPath(profileWorkspace));
        var profileLimited = Store(profileWorkspace, new ModelProfileMetadataStoreOptions { MaxProfiles = 1 });

        Assert.Equal(ModelProfileSourceReadStatus.Unavailable, (await profileLimited.ReadAsync(secondProfile.DescriptorIdentity.Id)).Status);
        Assert.Equal(profileContent, await File.ReadAllTextAsync(PrimaryPath(profileWorkspace)));
    }

    [Theory]
    [InlineData("unknown")]
    [InlineData("duplicate")]
    [InlineData("schema")]
    [InlineData("revision-hash")]
    [InlineData("bom")]
    [InlineData("invalid-utf8")]
    public async Task Corrupt_or_noncanonical_source_fails_closed_for_read_and_publish(string corruption)
    {
        using var workspace = new TestWorkspace();
        var metadata = ModelProfilePersistenceTestData.Metadata();
        Assert.Equal(ModelProfileMetadataPublishStatus.Published, (await Store(workspace).PublishAsync("seed-one", metadata, null)).Status);
        var path = PrimaryPath(workspace);
        var text = await File.ReadAllTextAsync(path);
        switch (corruption)
        {
            case "unknown":
                await File.WriteAllTextAsync(path, text.Replace("\"workspaceIdentity\":", "\"unknown\":true,\n  \"workspaceIdentity\":", StringComparison.Ordinal));
                break;
            case "duplicate":
                await File.WriteAllTextAsync(path, text.Replace("\"generation\": 1", "\"generation\": 1,\n  \"generation\": 1", StringComparison.Ordinal));
                break;
            case "schema":
                await File.WriteAllTextAsync(path, text.Replace("\"schemaVersion\": 1", "\"schemaVersion\": 2", StringComparison.Ordinal));
                break;
            case "revision-hash":
                var marker = "\"sourceRevisionHash\": \"";
                var index = text.IndexOf(marker, StringComparison.Ordinal) + marker.Length;
                Assert.True(index >= marker.Length);
                await File.WriteAllTextAsync(path, text[..index] + (text[index] == 'a' ? 'b' : 'a') + text[(index + 1)..]);
                break;
            case "bom":
                await File.WriteAllBytesAsync(path, [0xef, 0xbb, 0xbf, .. Encoding.UTF8.GetBytes(text)]);
                break;
            case "invalid-utf8":
                await File.WriteAllBytesAsync(path, [0xff, 0xfe, 0xfd]);
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(corruption));
        }

        var read = await Store(workspace).ReadAsync(metadata.DescriptorIdentity.Id);
        var publish = await Store(workspace).PublishAsync("seed-two", ModelProfilePersistenceTestData.Metadata(configurationRevision: 2, configurationHash: 'b'), new string('a', 64));

        Assert.Equal(ModelProfileSourceReadStatus.Unavailable, read.Status);
        Assert.Equal(ModelProfileMetadataPublishStatus.Unavailable, publish.Status);
        Assert.DoesNotContain("configurationRevision\": 2", await File.ReadAllTextAsync(path), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Interrupted_primary_publication_is_readable_and_exact_retry_finalizes_without_duplicate_revision()
    {
        using var workspace = new TestWorkspace();
        var options = new ModelProfileMetadataStoreOptions
        {
            DurableBoundaryObserver = (boundary, _) => boundary == ModelProfileMetadataPersistenceBoundary.PrimaryPublished
                ? ValueTask.FromException(new IOException("Injected process loss after primary publication."))
                : ValueTask.CompletedTask
        };
        var metadata = ModelProfilePersistenceTestData.Metadata();

        var interrupted = await Store(workspace, options).PublishAsync("seed-one", metadata, null);
        var recoverableRead = await Store(workspace).ReadAsync(metadata.DescriptorIdentity.Id);
        var retry = await Store(workspace).PublishAsync("seed-one", metadata, null);

        Assert.Equal(ModelProfileMetadataPublishStatus.Published, interrupted.Status);
        Assert.Equal(ModelProfileSourceReadStatus.Found, recoverableRead.Status);
        Assert.Equal(interrupted.SourceRevisionHash, recoverableRead.SourceRevisionHash);
        Assert.Equal(ModelProfileMetadataPublishStatus.AlreadyPresent, retry.Status);
        var content = await File.ReadAllTextAsync(PrimaryPath(workspace));
        Assert.Equal(1, Count(content, "\"operationId\": \"seed-one\""));
    }

    private static ModelProfileMetadataStore Store(TestWorkspace workspace, ModelProfileMetadataStoreOptions? options = null)
        => new(workspace.ServerStatePath, options);

    private static string PrimaryPath(TestWorkspace workspace) => Path.Combine(workspace.ServerStatePath, "model-profile-metadata", "catalog.json");

    private static string ProofPath(TestWorkspace workspace) => Path.Combine(workspace.ServerStatePath, "model-profile-metadata", "catalog.proved.json");

    private static int Count(string value, string fragment) => value.Split(fragment, StringSplitOptions.None).Length - 1;
}
