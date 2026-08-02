using System.Text.Json.Nodes;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using EmbodySense.Core.Application.Capabilities;
using EmbodySense.Core.Application.Capabilities.Models;
using EmbodySense.Core.Common.Workspace;
using EmbodySense.Core.Persistence.Capabilities;
using EmbodySense.Tests.Support;

namespace EmbodySense.Core.Persistence.Tests.Capabilities;

public sealed class CapabilityArtifactStoreTests
{
    private static readonly byte[] _versionOne = "version-one"u8.ToArray();
    private static readonly byte[] _versionTwo = "version-two"u8.ToArray();

    [Fact]
    public async Task Verified_artifact_stages_activates_and_survives_restart()
    {
        using var workspace = new TestWorkspace();
        var paths = new WorkspacePaths(workspace.RootPath);
        var store = Store(workspace, paths);
        var stage = CapabilityArtifactStoreTestData.Stage(_versionOne);

        Assert.Equal(CapabilityArtifactStoreStatus.Applied, (await store.StageAsync(stage)).Status);
        var activated = await store.ActivateAsync(new CapabilityArtifactActivationRequest(stage.Manifest, 0, "activate-v1"));
        var restarted = await Store(workspace, paths).ReadAsync(stage.Manifest.Descriptor.Id);

        Assert.Equal(CapabilityArtifactStoreStatus.Applied, activated.Status);
        Assert.Equal(1, activated.Activation!.Revision);
        Assert.Equal(stage.Manifest.Checksum, restarted.Activation!.ArtifactDigest);
        Assert.Equal(await File.ReadAllTextAsync(paths.CapabilityArtifactActivationPath), await File.ReadAllTextAsync(paths.CapabilityArtifactActivationProofPath));
    }

    [Fact]
    public async Task Duplicate_stage_and_exact_activation_operation_are_idempotent()
    {
        using var workspace = new TestWorkspace();
        var store = Store(workspace);
        var stage = CapabilityArtifactStoreTestData.Stage(_versionOne);

        Assert.Equal(CapabilityArtifactStoreStatus.Applied, (await store.StageAsync(stage)).Status);
        Assert.Equal(CapabilityArtifactStoreStatus.NoChange, (await store.StageAsync(stage)).Status);
        Assert.Equal(CapabilityArtifactStoreStatus.Applied, (await store.ActivateAsync(new CapabilityArtifactActivationRequest(stage.Manifest, 0, "activate"))).Status);
        Assert.Equal(CapabilityArtifactStoreStatus.Replayed, (await store.ActivateAsync(new CapabilityArtifactActivationRequest(stage.Manifest, 0, "activate"))).Status);
    }

    [Fact]
    public async Task Operation_reuse_and_stale_revision_fail_without_replacing_current()
    {
        using var workspace = new TestWorkspace();
        var store = Store(workspace);
        var first = CapabilityArtifactStoreTestData.Stage(_versionOne);
        var second = CapabilityArtifactStoreTestData.Stage(_versionTwo, "2.0.0");
        await store.StageAsync(first);
        await store.StageAsync(second);
        await store.ActivateAsync(new CapabilityArtifactActivationRequest(first.Manifest, 0, "activate"));

        var reused = await store.ActivateAsync(new CapabilityArtifactActivationRequest(second.Manifest, 1, "activate"));
        var stale = await store.ActivateAsync(new CapabilityArtifactActivationRequest(second.Manifest, 0, "activate-v2"));
        var current = await store.ReadAsync(first.Manifest.Descriptor.Id);

        Assert.Equal(CapabilityArtifactStoreStatus.Invalid, reused.Status);
        Assert.Equal(CapabilityArtifactStoreStatus.Conflict, stale.Status);
        Assert.Equal(first.Manifest.Checksum, current.Activation!.ArtifactDigest);
    }

    [Fact]
    public async Task Full_idempotency_ledger_refuses_new_operations_without_evicting_old_bindings()
    {
        using var workspace = new TestWorkspace();
        var store = Store(workspace);
        var first = CapabilityArtifactStoreTestData.Stage(_versionOne);
        var second = CapabilityArtifactStoreTestData.Stage(_versionTwo, "2.0.0");
        await store.StageAsync(first);
        await store.StageAsync(second);
        for (var revision = 0; revision < 256; revision++)
        {
            var stage = revision % 2 == 0 ? first : second;
            Assert.Equal(CapabilityArtifactStoreStatus.Applied, (await store.ActivateAsync(new CapabilityArtifactActivationRequest(stage.Manifest, revision, $"operation-{revision}"))).Status);
        }

        Assert.Equal(CapabilityArtifactStoreStatus.Unavailable, (await store.ActivateAsync(new CapabilityArtifactActivationRequest(first.Manifest, 256, "operation-new"))).Status);
        Assert.Equal(CapabilityArtifactStoreStatus.Replayed, (await store.ActivateAsync(new CapabilityArtifactActivationRequest(first.Manifest, 0, "operation-0"))).Status);
    }

    [Fact]
    public async Task Rollback_restores_immediately_prior_proved_artifact_and_is_replayable()
    {
        using var workspace = new TestWorkspace();
        var store = Store(workspace);
        var first = CapabilityArtifactStoreTestData.Stage(_versionOne);
        var second = CapabilityArtifactStoreTestData.Stage(_versionTwo, "2.0.0");
        await store.StageAsync(first);
        await store.StageAsync(second);
        await store.ActivateAsync(new CapabilityArtifactActivationRequest(first.Manifest, 0, "activate-v1"));
        await store.ActivateAsync(new CapabilityArtifactActivationRequest(second.Manifest, 1, "activate-v2"));

        var rolledBack = await store.RollbackAsync(first.Manifest.Descriptor.Id, 2, "rollback-v1");
        var replayed = await store.RollbackAsync(first.Manifest.Descriptor.Id, 2, "rollback-v1");

        Assert.Equal(CapabilityArtifactStoreStatus.Applied, rolledBack.Status);
        Assert.Equal(first.Manifest.Checksum, rolledBack.Activation!.ArtifactDigest);
        Assert.Equal(second.Manifest.Checksum, rolledBack.Activation.PriorArtifactDigest);
        Assert.Equal(CapabilityArtifactStoreStatus.Replayed, replayed.Status);
    }

    [Fact]
    public async Task Missing_prior_artifact_cannot_roll_back()
    {
        using var workspace = new TestWorkspace();
        var store = Store(workspace);
        var stage = CapabilityArtifactStoreTestData.Stage(_versionOne);
        await store.StageAsync(stage);
        await store.ActivateAsync(new CapabilityArtifactActivationRequest(stage.Manifest, 0, "activate"));

        var result = await store.RollbackAsync(stage.Manifest.Descriptor.Id, 1, "rollback");

        Assert.Equal(CapabilityArtifactStoreStatus.NotFound, result.Status);
        Assert.Equal(stage.Manifest.Checksum, result.Activation!.ArtifactDigest);
    }

    [Fact]
    public async Task Tampered_bytes_cannot_stage_or_replace_current_activation()
    {
        using var workspace = new TestWorkspace();
        var store = Store(workspace);
        var stage = CapabilityArtifactStoreTestData.Stage(_versionOne);
        await store.StageAsync(stage);
        await store.ActivateAsync(new CapabilityArtifactActivationRequest(stage.Manifest, 0, "activate"));
        var tampered = stage with { Content = new CapabilityArtifactContent("tampered"u8) };

        var result = await store.StageAsync(tampered);
        var current = await store.ReadAsync(stage.Manifest.Descriptor.Id);

        Assert.Equal(CapabilityArtifactStoreStatus.Invalid, result.Status);
        Assert.Equal(stage.Manifest.Checksum, current.Activation!.ArtifactDigest);
    }

    [Fact]
    public async Task Caller_supplied_verified_claim_cannot_bypass_server_owned_reverification()
    {
        using var workspace = new TestWorkspace();
        var store = Store(workspace, verifier: new RejectingArtifactVerifier());
        var stage = CapabilityArtifactStoreTestData.Stage(_versionOne) with { Trust = new CapabilityArtifactTrustDecision(CapabilityArtifactTrustStatus.Verified, "forged", "Forged.") };

        var result = await store.StageAsync(stage);

        Assert.Equal(CapabilityArtifactStoreStatus.Invalid, result.Status);
    }

    [Fact]
    public async Task Corrupt_primary_recovers_last_proof_read_only_and_blocks_mutation()
    {
        using var workspace = new TestWorkspace();
        var paths = new WorkspacePaths(workspace.RootPath);
        var store = Store(workspace, paths);
        var first = CapabilityArtifactStoreTestData.Stage(_versionOne);
        var second = CapabilityArtifactStoreTestData.Stage(_versionTwo, "2.0.0");
        await store.StageAsync(first);
        await store.StageAsync(second);
        await store.ActivateAsync(new CapabilityArtifactActivationRequest(first.Manifest, 0, "activate-v1"));
        await File.WriteAllTextAsync(paths.CapabilityArtifactActivationPath, "{ forged }");

        var recovered = await store.ReadAsync(first.Manifest.Descriptor.Id);
        var mutation = await store.ActivateAsync(new CapabilityArtifactActivationRequest(second.Manifest, 1, "activate-v2"));

        Assert.Equal(first.Manifest.Checksum, recovered.Activation!.ArtifactDigest);
        Assert.Equal(CapabilityArtifactStoreStatus.Unavailable, mutation.Status);
        Assert.Equal(first.Manifest.Checksum, (await store.ReadAsync(first.Manifest.Descriptor.Id)).Activation!.ArtifactDigest);
    }

    [Fact]
    public async Task Forged_self_digest_is_rejected_and_does_not_replace_proof()
    {
        using var workspace = new TestWorkspace();
        var paths = new WorkspacePaths(workspace.RootPath);
        var store = Store(workspace, paths);
        var stage = CapabilityArtifactStoreTestData.Stage(_versionOne);
        await store.StageAsync(stage);
        await store.ActivateAsync(new CapabilityArtifactActivationRequest(stage.Manifest, 0, "activate"));
        var primary = JsonNode.Parse(await File.ReadAllTextAsync(paths.CapabilityArtifactActivationPath))!.AsObject();
        primary["revision"] = 999;
        await File.WriteAllTextAsync(paths.CapabilityArtifactActivationPath, primary.ToJsonString());

        var read = await store.ReadAsync(stage.Manifest.Descriptor.Id);

        Assert.Equal(1, read.Activation!.Revision);
    }

    [Fact]
    public async Task Forged_primary_and_proof_with_recomputed_unkeyed_digest_fail_server_owned_authentication()
    {
        using var workspace = new TestWorkspace();
        var paths = new WorkspacePaths(workspace.RootPath);
        var store = Store(workspace, paths);
        var stage = CapabilityArtifactStoreTestData.Stage(_versionOne);
        await store.StageAsync(stage);
        await store.ActivateAsync(new CapabilityArtifactActivationRequest(stage.Manifest, 0, "activate"));
        var forged = JsonNode.Parse(await File.ReadAllTextAsync(paths.CapabilityArtifactActivationPath))!.AsObject();
        forged["revision"] = 999;
        forged["authenticationTag"] = string.Empty;
        forged["contentDigest"] = string.Empty;
        var compact = forged.ToJsonString(new JsonSerializerOptions(JsonSerializerDefaults.Web) { WriteIndented = true });
        forged["contentDigest"] = "sha256:" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(compact))).ToLowerInvariant();
        var forgedJson = forged.ToJsonString(new JsonSerializerOptions(JsonSerializerDefaults.Web) { WriteIndented = true });
        await File.WriteAllTextAsync(paths.CapabilityArtifactActivationPath, forgedJson);
        await File.WriteAllTextAsync(paths.CapabilityArtifactActivationProofPath, forgedJson);

        var read = await store.ReadAsync(stage.Manifest.Descriptor.Id);

        Assert.Equal(CapabilityArtifactStoreStatus.Unavailable, read.Status);
        Assert.Null(read.Activation);
    }

    [Theory]
    [InlineData("case-alias")]
    [InlineData("duplicate")]
    public async Task Structurally_ambiguous_primary_recovers_the_authenticated_proof(string mutation)
    {
        using var workspace = new TestWorkspace();
        var paths = new WorkspacePaths(workspace.RootPath);
        var store = Store(workspace, paths);
        var stage = CapabilityArtifactStoreTestData.Stage(_versionOne);
        await store.StageAsync(stage);
        await store.ActivateAsync(new CapabilityArtifactActivationRequest(stage.Manifest, 0, "activate"));
        var canonical = await File.ReadAllTextAsync(paths.CapabilityArtifactActivationPath);
        var malformed = mutation == "case-alias"
            ? canonical.Replace("\"schemaVersion\":", "\"SchemaVersion\":", StringComparison.Ordinal)
            : canonical.Replace("\"revision\": 1,", "\"revision\": 1,\n  \"revision\": 1,", StringComparison.Ordinal);
        await File.WriteAllTextAsync(paths.CapabilityArtifactActivationPath, malformed);

        var read = await store.ReadAsync(stage.Manifest.Descriptor.Id);

        Assert.Equal(CapabilityArtifactStoreStatus.Applied, read.Status);
        Assert.NotNull(read.Activation);
        Assert.Equal(1, read.Activation.Revision);
        Assert.True(stage.Manifest.Checksum.FixedTimeEquals(read.Activation.ArtifactDigest));
    }

    [Theory]
    [InlineData("case-alias")]
    [InlineData("duplicate")]
    public async Task Structurally_ambiguous_activation_documents_fail_closed(string mutation)
    {
        using var workspace = new TestWorkspace();
        var paths = new WorkspacePaths(workspace.RootPath);
        var store = Store(workspace, paths);
        var stage = CapabilityArtifactStoreTestData.Stage(_versionOne);
        await store.StageAsync(stage);
        await store.ActivateAsync(new CapabilityArtifactActivationRequest(stage.Manifest, 0, "activate"));
        var canonical = await File.ReadAllTextAsync(paths.CapabilityArtifactActivationPath);
        var malformed = mutation == "case-alias"
            ? canonical.Replace("\"schemaVersion\":", "\"SchemaVersion\":", StringComparison.Ordinal)
            : canonical.Replace("\"revision\": 1,", "\"revision\": 1,\n  \"revision\": 1,", StringComparison.Ordinal);
        await File.WriteAllTextAsync(paths.CapabilityArtifactActivationPath, malformed);
        await File.WriteAllTextAsync(paths.CapabilityArtifactActivationProofPath, malformed);

        var read = await store.ReadAsync(stage.Manifest.Descriptor.Id);
        var mutationResult = await store.ActivateAsync(new CapabilityArtifactActivationRequest(stage.Manifest, 1, "activate-next"));

        Assert.Equal(CapabilityArtifactStoreStatus.Unavailable, read.Status);
        Assert.Null(read.Activation);
        Assert.Equal(CapabilityArtifactStoreStatus.Unavailable, mutationResult.Status);
    }

    [Theory]
    [InlineData("case-alias")]
    [InlineData("duplicate")]
    public async Task Structurally_ambiguous_staged_evidence_cannot_activate(string mutation)
    {
        using var workspace = new TestWorkspace();
        var paths = new WorkspacePaths(workspace.RootPath);
        var store = Store(workspace, paths);
        var stage = CapabilityArtifactStoreTestData.Stage(_versionOne);
        await store.StageAsync(stage);
        var digestName = stage.Manifest.Checksum.Value["sha256:".Length..];
        var evidencePath = Path.Combine(paths.CapabilityArtifactsPath, "staged", digestName, "artifact.evidence.json");
        var canonical = await File.ReadAllTextAsync(evidencePath);
        var malformed = mutation == "case-alias"
            ? canonical.Replace("\"capabilityId\":", "\"CapabilityId\":", StringComparison.Ordinal)
            : canonical.Replace("\"capabilityId\":", "\"capabilityId\": \"capability/test\",\n  \"capabilityId\":", StringComparison.Ordinal);
        await File.WriteAllTextAsync(evidencePath, malformed);

        var activation = await store.ActivateAsync(new CapabilityArtifactActivationRequest(stage.Manifest, 0, "activate"));

        Assert.Equal(CapabilityArtifactStoreStatus.NotFound, activation.Status);
        Assert.Null(activation.Activation);
    }

    [Fact]
    public async Task Partial_or_conflicting_staged_content_never_activates()
    {
        using var workspace = new TestWorkspace();
        var paths = new WorkspacePaths(workspace.RootPath);
        var store = Store(workspace, paths);
        var stage = CapabilityArtifactStoreTestData.Stage(_versionOne);
        var digestName = stage.Manifest.Checksum.Value["sha256:".Length..];
        var root = Path.Combine(paths.CapabilityArtifactsPath, "staged", digestName);
        Directory.CreateDirectory(root);
        await File.WriteAllTextAsync(Path.Combine(root, "artifact.evidence.json"), "forged");

        Assert.Equal(CapabilityArtifactStoreStatus.Invalid, (await store.StageAsync(stage)).Status);
        Assert.Equal(CapabilityArtifactStoreStatus.NotFound, (await store.ActivateAsync(new CapabilityArtifactActivationRequest(stage.Manifest, 0, "activate"))).Status);
    }

    [Fact]
    public async Task Artifact_store_files_do_not_modify_catalog_document_or_lifecycle_axes()
    {
        using var workspace = new TestWorkspace();
        var paths = new WorkspacePaths(workspace.RootPath);
        var catalogSentinel = "catalog-owned-state";
        Directory.CreateDirectory(paths.CapabilityCatalogPath);
        await File.WriteAllTextAsync(paths.CapabilityCatalogDocumentPath, catalogSentinel);
        var store = Store(workspace, paths);
        var stage = CapabilityArtifactStoreTestData.Stage(_versionOne);

        await store.StageAsync(stage);
        await store.ActivateAsync(new CapabilityArtifactActivationRequest(stage.Manifest, 0, "activate"));

        Assert.Equal(catalogSentinel, await File.ReadAllTextAsync(paths.CapabilityCatalogDocumentPath));
    }

    [Fact]
    public async Task Invalid_requests_and_conflicting_immutable_content_fail_closed()
    {
        using var workspace = new TestWorkspace();
        var paths = new WorkspacePaths(workspace.RootPath);
        var store = Store(workspace, paths);
        var stage = CapabilityArtifactStoreTestData.Stage(_versionOne);
        var invalid = stage with { Manifest = stage.Manifest with { SchemaVersion = 2 } };

        Assert.Equal(CapabilityArtifactStoreStatus.Invalid, (await store.StageAsync(invalid)).Status);
        Assert.Equal(CapabilityArtifactStoreStatus.Invalid, (await store.ActivateAsync(new CapabilityArtifactActivationRequest(stage.Manifest, -1, "activate"))).Status);
        Assert.Equal(CapabilityArtifactStoreStatus.Invalid, (await store.RollbackAsync(stage.Manifest.Descriptor.Id, -1, "rollback")).Status);

        await store.StageAsync(stage);
        var digestName = stage.Manifest.Checksum.Value["sha256:".Length..];
        var contentPath = Path.Combine(paths.CapabilityArtifactsPath, "staged", digestName, stage.Manifest.EntryPoint);
        await File.WriteAllBytesAsync(contentPath, _versionTwo);

        Assert.Equal(CapabilityArtifactStoreStatus.Invalid, (await store.StageAsync(stage)).Status);
    }

    [Fact]
    public async Task Stale_and_unproved_rollback_requests_preserve_current_activation()
    {
        using var workspace = new TestWorkspace();
        var paths = new WorkspacePaths(workspace.RootPath);
        var store = Store(workspace, paths);
        var first = CapabilityArtifactStoreTestData.Stage(_versionOne);
        var second = CapabilityArtifactStoreTestData.Stage(_versionTwo, "2.0.0");
        await store.StageAsync(first);
        await store.StageAsync(second);
        await store.ActivateAsync(new CapabilityArtifactActivationRequest(first.Manifest, 0, "activate-v1"));
        await store.ActivateAsync(new CapabilityArtifactActivationRequest(second.Manifest, 1, "activate-v2"));

        Assert.Equal(CapabilityArtifactStoreStatus.Conflict, (await store.RollbackAsync(first.Manifest.Descriptor.Id, 1, "stale-rollback")).Status);

        await File.WriteAllTextAsync(paths.CapabilityArtifactActivationPath, "{ forged }");
        Assert.Equal(CapabilityArtifactStoreStatus.Unavailable, (await store.RollbackAsync(first.Manifest.Descriptor.Id, 2, "unproved-rollback")).Status);
    }

    [Fact]
    public async Task Resolved_execution_lease_retains_the_exact_proved_executable_identity()
    {
        using var workspace = new TestWorkspace();
        var paths = new WorkspacePaths(workspace.RootPath);
        var store = Store(workspace, paths);
        var stage = CapabilityArtifactStoreTestData.Stage(_versionOne);
        await store.StageAsync(stage);
        await store.ActivateAsync(new CapabilityArtifactActivationRequest(stage.Manifest, 0, "activate-for-execution"));

        var resolution = await store.ResolveAsync(new CapabilityExecutableInvocation(stage.Manifest, "caller-controlled-root", "{}", "resolve-execution", 1));

        Assert.Equal(CapabilityExecutableAvailabilityStatus.Available, resolution.Status);
        var lease = Assert.IsAssignableFrom<ICapabilityExecutableArtifactLease>(resolution.Lease);
        var executablePath = lease.ExecutablePath;
        await using (lease)
        {
            Assert.Equal(stage.Manifest.Checksum, lease.ArtifactDigest);
            Assert.Equal(1, lease.ActivationRevision);
            Assert.DoesNotContain("caller-controlled-root", lease.ExecutablePath, StringComparison.Ordinal);
            var retained = new byte[_versionOne.Length];
            Assert.Equal(retained.Length, RandomAccess.Read(lease.ExecutableHandle, retained, 0));
            Assert.Equal(_versionOne, retained);
            if (OperatingSystem.IsWindows())
            {
                Assert.Throws<IOException>(() => File.WriteAllBytes(lease.ExecutablePath, _versionTwo));
            }
        }
        if (OperatingSystem.IsWindows())
        {
            File.WriteAllBytes(executablePath, _versionOne);
            Assert.Equal(_versionOne, File.ReadAllBytes(executablePath));
        }
    }

    [Fact]
    public async Task Execution_resolution_rejects_stale_revision_and_digest_evidence()
    {
        using var workspace = new TestWorkspace();
        var store = Store(workspace);
        var first = CapabilityArtifactStoreTestData.Stage(_versionOne);
        var second = CapabilityArtifactStoreTestData.Stage(_versionTwo, "2.0.0");
        await store.StageAsync(first);
        await store.StageAsync(second);
        await store.ActivateAsync(new CapabilityArtifactActivationRequest(first.Manifest, 0, "activate-v1-for-resolution"));

        var stale = await store.ResolveAsync(new CapabilityExecutableInvocation(first.Manifest, string.Empty, "{}", "resolve-stale", 2));
        var wrongDigest = await store.ResolveAsync(new CapabilityExecutableInvocation(second.Manifest, string.Empty, "{}", "resolve-wrong-digest", 1));

        Assert.Equal(CapabilityExecutableAvailabilityStatus.Unavailable, stale.Status);
        Assert.Null(stale.Lease);
        Assert.Equal(CapabilityExecutableAvailabilityStatus.Unavailable, wrongDigest.Status);
        Assert.Null(wrongDigest.Lease);
    }

    private static CapabilityArtifactStore Store(TestWorkspace workspace, WorkspacePaths? paths = null, ICapabilityArtifactTrustVerifier? verifier = null) => new(paths ?? new WorkspacePaths(workspace.RootPath), new FileCapabilityArtifactStateTrustProvider(workspace.ServerStatePath), verifier ?? new AlwaysTrustedArtifactVerifier());

    private sealed class AlwaysTrustedArtifactVerifier : ICapabilityArtifactTrustVerifier
    {
        public Task<CapabilityArtifactTrustDecision> VerifyAsync(CapabilityArtifactManifest manifest, EmbodySense.Core.Common.Capabilities.CapabilityIntegrityDigest actualDigest, CancellationToken cancellationToken = default) => Task.FromResult(new CapabilityArtifactTrustDecision(CapabilityArtifactTrustStatus.Verified, "test-server-policy", "Verified."));
    }

    private sealed class RejectingArtifactVerifier : ICapabilityArtifactTrustVerifier
    {
        public Task<CapabilityArtifactTrustDecision> VerifyAsync(CapabilityArtifactManifest manifest, EmbodySense.Core.Common.Capabilities.CapabilityIntegrityDigest actualDigest, CancellationToken cancellationToken = default) => Task.FromResult(new CapabilityArtifactTrustDecision(CapabilityArtifactTrustStatus.Rejected, "test-server-policy", "Rejected."));
    }
}
