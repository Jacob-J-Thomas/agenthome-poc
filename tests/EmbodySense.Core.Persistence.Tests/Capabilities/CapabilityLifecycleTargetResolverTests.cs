using System.Security.Cryptography;
using System.Runtime.InteropServices;
using System.Text.Json.Nodes;
using EmbodySense.Core.Application.Capabilities.Models;
using EmbodySense.Core.Common.Workspace;
using EmbodySense.Core.Persistence.Capabilities;
using EmbodySense.Tests.Support;

namespace EmbodySense.Core.Persistence.Tests.Capabilities;

public sealed class CapabilityLifecycleTargetResolverTests
{
    private static readonly byte[] _versionOne = "resolver-version-one"u8.ToArray();
    private static readonly byte[] _versionTwo = "resolver-version-two"u8.ToArray();

    [Fact]
    public async Task Complete_scan_resolves_zero_one_multiple_and_exact_version_filter()
    {
        using var workspace = new TestWorkspace();
        var paths = PrepareEmpty(workspace);
        var store = Store(workspace, paths);
        var first = CapabilityArtifactStoreTestData.Stage(_versionOne);
        var second = CapabilityArtifactStoreTestData.Stage(_versionTwo, "2.0.0");
        var request = new CapabilityLifecycleTargetResolutionRequest(CapabilityLifecycleOperationKind.Upgrade, first.Manifest.Descriptor.Id);

        Assert.Equal(CapabilityLifecycleTargetResolutionStatus.NotFound, (await store.ResolveAsync(request)).Status);
        Assert.Equal(CapabilityArtifactStoreStatus.Applied, (await store.StageAsync(first)).Status);
        var single = await store.ResolveAsync(request);
        Assert.Equal(CapabilityLifecycleTargetResolutionStatus.Available, single.Status);
        Assert.Equal(first.Manifest.Descriptor.Id, single.Descriptor!.Id);
        Assert.Equal(first.Manifest.Descriptor.Version, single.Descriptor.Version);
        Assert.Equal(first.Manifest.Checksum, single.ArtifactDigest);
        Assert.Equal(CapabilityArtifactStoreStatus.Applied, (await store.StageAsync(second)).Status);
        Assert.Equal(CapabilityLifecycleTargetResolutionStatus.Ambiguous, (await store.ResolveAsync(request)).Status);
        var filtered = await store.ResolveAsync(request with { TargetVersion = second.Manifest.Descriptor.Version });
        Assert.Equal(CapabilityLifecycleTargetResolutionStatus.Available, filtered.Status);
        Assert.Equal(second.Manifest.Checksum, filtered.ArtifactDigest);
    }

    [Theory]
    [InlineData("capabilityId")]
    [InlineData("capabilityVersion")]
    [InlineData("descriptorJson")]
    [InlineData("checksum")]
    [InlineData("sourceKind")]
    [InlineData("updatePolicy")]
    [InlineData("platform")]
    [InlineData("trustStatus")]
    [InlineData("authenticationTag")]
    public async Task Forged_identity_content_or_trust_evidence_makes_the_complete_set_unavailable(string property)
    {
        using var workspace = new TestWorkspace();
        var paths = new WorkspacePaths(workspace.RootPath);
        var store = Store(workspace, paths);
        var stage = CapabilityArtifactStoreTestData.Stage(_versionOne);
        await store.StageAsync(stage);
        var evidencePath = EvidencePath(paths, stage);
        var evidence = JsonNode.Parse(await File.ReadAllTextAsync(evidencePath))!.AsObject();
        evidence[property] = property switch
        {
            "capabilityId" => "org.example/forged",
            "capabilityVersion" => "9.9.9",
            "descriptorJson" => "{}",
            "checksum" => "sha256:" + new string('0', 64),
            "sourceKind" => "Unknown",
            "updatePolicy" => "Unknown",
            "platform" => "unknown",
            "trustStatus" => "Rejected",
            _ => string.Empty
        };
        await File.WriteAllTextAsync(evidencePath, evidence.ToJsonString());

        var result = await store.ResolveAsync(new CapabilityLifecycleTargetResolutionRequest(CapabilityLifecycleOperationKind.Enable, stage.Manifest.Descriptor.Id));

        Assert.Equal(CapabilityLifecycleTargetResolutionStatus.Unavailable, result.Status);
        Assert.Null(result.Descriptor);
        Assert.Null(result.ArtifactDigest);
    }

    [Fact]
    public async Task Missing_invalid_nested_and_overfull_staged_layouts_fail_closed()
    {
        var stage = CapabilityArtifactStoreTestData.Stage(_versionOne);

        using var missingWorkspace = new TestWorkspace();
        var missingPaths = new WorkspacePaths(missingWorkspace.RootPath);
        Directory.CreateDirectory(missingPaths.CapabilityArtifactsPath);
        Assert.Equal(CapabilityLifecycleTargetResolutionStatus.NotFound, (await Store(missingWorkspace, missingPaths).ResolveAsync(Request(stage))).Status);

        using var invalidWorkspace = new TestWorkspace();
        var invalidPaths = PrepareEmpty(invalidWorkspace);
        Directory.CreateDirectory(Path.Combine(invalidPaths.CapabilityArtifactsPath, "staged", "not-a-canonical-digest"));
        Assert.Equal(CapabilityLifecycleTargetResolutionStatus.Unavailable, (await Store(invalidWorkspace, invalidPaths).ResolveAsync(Request(stage))).Status);

        using var nestedWorkspace = new TestWorkspace();
        var nestedPaths = new WorkspacePaths(nestedWorkspace.RootPath);
        var nestedStore = Store(nestedWorkspace, nestedPaths);
        var nestedStage = stage with { Manifest = stage.Manifest with { EntryPoint = "bin/echo.exe" } };
        Assert.Equal(CapabilityArtifactStoreStatus.Applied, (await nestedStore.StageAsync(nestedStage)).Status);
        var nestedDirectory = Path.Combine(StagedRoot(nestedPaths.CapabilityArtifactsPath, nestedStage), "bin");
        Directory.Delete(nestedDirectory, recursive: true);
        await File.WriteAllTextAsync(nestedDirectory, "substituted-directory");
        Assert.Equal(CapabilityLifecycleTargetResolutionStatus.Unavailable, (await nestedStore.ResolveAsync(Request(nestedStage))).Status);

        using var overfullWorkspace = new TestWorkspace();
        var overfullPaths = new WorkspacePaths(overfullWorkspace.RootPath);
        var overfullStore = Store(overfullWorkspace, overfullPaths);
        Assert.Equal(CapabilityArtifactStoreStatus.Applied, (await overfullStore.StageAsync(stage)).Status);
        var overfullRoot = StagedRoot(overfullPaths.CapabilityArtifactsPath, stage);
        for (var index = 0; index < 63; index++)
        {
            await File.WriteAllTextAsync(Path.Combine(overfullRoot, $"extra-{index:D2}.bin"), "extra");
        }
        Assert.Equal(CapabilityLifecycleTargetResolutionStatus.Unavailable, (await overfullStore.ResolveAsync(Request(stage))).Status);

        using var deepWorkspace = new TestWorkspace();
        var deepPaths = new WorkspacePaths(deepWorkspace.RootPath);
        var deepStore = Store(deepWorkspace, deepPaths);
        var deepStage = stage with { Manifest = stage.Manifest with { EntryPoint = string.Join('/', Enumerable.Repeat("d", 64)) } };
        Assert.Equal(CapabilityArtifactStoreStatus.Applied, (await deepStore.StageAsync(deepStage)).Status);
        Assert.Equal(CapabilityLifecycleTargetResolutionStatus.Unavailable, (await deepStore.ResolveAsync(Request(deepStage))).Status);
    }

    [Fact]
    public async Task Aggregate_evidence_quota_and_mid_scan_cancellation_fail_closed()
    {
        using var quotaWorkspace = new TestWorkspace();
        var quotaPaths = new WorkspacePaths(quotaWorkspace.RootPath);
        var quotaStore = Store(quotaWorkspace, quotaPaths);
        CapabilityArtifactStageRequest? first = null;
        for (var index = 0; index < 5; index++)
        {
            var stage = CapabilityArtifactStoreTestData.Stage(BitConverter.GetBytes(index), $"1.0.{index}");
            first ??= stage;
            Assert.Equal(CapabilityArtifactStoreStatus.Applied, (await quotaStore.StageAsync(stage)).Status);
            var evidencePath = EvidencePath(quotaPaths, stage);
            var evidenceLength = new FileInfo(evidencePath).Length;
            Assert.InRange(evidenceLength, 1, 1_048_575);
            await File.AppendAllTextAsync(evidencePath, new string(' ', checked(1_048_576 - (int)evidenceLength)));
        }
        Assert.Equal(CapabilityLifecycleTargetResolutionStatus.Unavailable, (await quotaStore.ResolveAsync(Request(first!))).Status);

        using var canceledWorkspace = new TestWorkspace();
        var canceledPaths = new WorkspacePaths(canceledWorkspace.RootPath);
        var canceledStage = CapabilityArtifactStoreTestData.Stage(_versionOne);
        Assert.Equal(CapabilityArtifactStoreStatus.Applied, (await Store(canceledWorkspace, canceledPaths).StageAsync(canceledStage)).Status);
        using var cancellation = new CancellationTokenSource();
        var canceledResolver = Store(canceledWorkspace, canceledPaths, new CancelingCapabilityCatalogPathObserver(cancellation));
        await Assert.ThrowsAsync<OperationCanceledException>(() => canceledResolver.ResolveAsync(Request(canceledStage), cancellation.Token));
    }

    [Fact]
    public async Task Malformed_duplicate_noncanonical_unexpected_and_cross_workspace_evidence_fail_closed()
    {
        using var malformedWorkspace = new TestWorkspace();
        var malformedPaths = new WorkspacePaths(malformedWorkspace.RootPath);
        var malformedStore = Store(malformedWorkspace, malformedPaths);
        var stage = CapabilityArtifactStoreTestData.Stage(_versionOne);
        await malformedStore.StageAsync(stage);
        var evidencePath = EvidencePath(malformedPaths, stage);
        var json = await File.ReadAllTextAsync(evidencePath);
        await File.WriteAllTextAsync(evidencePath, json.Replace("\"schemaVersion\": 1,", "\"schemaVersion\": 1,\n  \"schemaVersion\": 1,", StringComparison.Ordinal));
        Assert.Equal(CapabilityLifecycleTargetResolutionStatus.Unavailable, (await malformedStore.ResolveAsync(Request(stage))).Status);

        using var unexpectedWorkspace = new TestWorkspace();
        var unexpectedPaths = new WorkspacePaths(unexpectedWorkspace.RootPath);
        var unexpectedStore = Store(unexpectedWorkspace, unexpectedPaths);
        await unexpectedStore.StageAsync(stage);
        await File.WriteAllTextAsync(Path.Combine(unexpectedPaths.CapabilityArtifactsPath, "staged", "unexpected.txt"), "unexpected");
        Assert.Equal(CapabilityLifecycleTargetResolutionStatus.Unavailable, (await unexpectedStore.ResolveAsync(Request(stage))).Status);

        using var foreignWorkspace = new TestWorkspace();
        var foreignPaths = new WorkspacePaths(foreignWorkspace.RootPath);
        Directory.CreateDirectory(foreignPaths.CapabilityArtifactsPath);
        Directory.Move(Path.Combine(unexpectedPaths.CapabilityArtifactsPath, "staged", stage.Manifest.Checksum.Value["sha256:".Length..]), Path.Combine(foreignPaths.CapabilityArtifactsPath, "staged-candidate"));
        Directory.CreateDirectory(Path.Combine(foreignPaths.CapabilityArtifactsPath, "staged"));
        Directory.Move(Path.Combine(foreignPaths.CapabilityArtifactsPath, "staged-candidate"), Path.Combine(foreignPaths.CapabilityArtifactsPath, "staged", stage.Manifest.Checksum.Value["sha256:".Length..]));
        Assert.Equal(CapabilityLifecycleTargetResolutionStatus.Unavailable, (await Store(foreignWorkspace, foreignPaths).ResolveAsync(Request(stage))).Status);
    }

    [Fact]
    public async Task Linked_substituted_or_extra_artifact_content_never_resolves()
    {
        using var extraWorkspace = new TestWorkspace();
        var extraPaths = new WorkspacePaths(extraWorkspace.RootPath);
        var extraStore = Store(extraWorkspace, extraPaths);
        var stage = CapabilityArtifactStoreTestData.Stage(_versionOne);
        await extraStore.StageAsync(stage);
        await File.WriteAllTextAsync(Path.Combine(Path.GetDirectoryName(EvidencePath(extraPaths, stage))!, "extra.bin"), "extra");
        Assert.Equal(CapabilityLifecycleTargetResolutionStatus.Unavailable, (await extraStore.ResolveAsync(Request(stage))).Status);

        using var substitutedWorkspace = new TestWorkspace();
        var substitutedPaths = new WorkspacePaths(substitutedWorkspace.RootPath);
        var substitutedStore = Store(substitutedWorkspace, substitutedPaths);
        await substitutedStore.StageAsync(stage);
        await File.WriteAllTextAsync(ContentPath(substitutedPaths, stage), "substituted");
        Assert.Equal(CapabilityLifecycleTargetResolutionStatus.Unavailable, (await substitutedStore.ResolveAsync(Request(stage))).Status);

        if (!OperatingSystem.IsWindows())
        {
            using var linkedWorkspace = new TestWorkspace();
            var linkedPaths = new WorkspacePaths(linkedWorkspace.RootPath);
            var linkedStore = Store(linkedWorkspace, linkedPaths);
            await linkedStore.StageAsync(stage);
            var contentPath = ContentPath(linkedPaths, stage);
            var retained = Path.Combine(linkedPaths.CapabilityArtifactsPath, "retained-content");
            File.Move(contentPath, retained);
            Assert.Equal(0, CreateHardLinkUnix(retained, contentPath));
            Assert.Equal(CapabilityLifecycleTargetResolutionStatus.Unavailable, (await linkedStore.ResolveAsync(Request(stage))).Status);

            using var symbolicWorkspace = new TestWorkspace();
            var symbolicPaths = new WorkspacePaths(symbolicWorkspace.RootPath);
            var symbolicStore = Store(symbolicWorkspace, symbolicPaths);
            await symbolicStore.StageAsync(stage);
            var symbolicContent = ContentPath(symbolicPaths, stage);
            var symbolicRetained = Path.Combine(symbolicPaths.CapabilityArtifactsPath, "symbolic-retained-content");
            File.Move(symbolicContent, symbolicRetained);
            File.CreateSymbolicLink(symbolicContent, symbolicRetained);
            Assert.Equal(CapabilityLifecycleTargetResolutionStatus.Unavailable, (await symbolicStore.ResolveAsync(Request(stage))).Status);

            using var specialWorkspace = new TestWorkspace();
            var specialPaths = new WorkspacePaths(specialWorkspace.RootPath);
            var specialStore = Store(specialWorkspace, specialPaths);
            await specialStore.StageAsync(stage);
            var specialContent = ContentPath(specialPaths, stage);
            File.Delete(specialContent);
            Assert.Equal(0, CreateFifoUnix(specialContent, 0x180));
            Assert.Equal(CapabilityLifecycleTargetResolutionStatus.Unavailable, (await specialStore.ResolveAsync(Request(stage))).Status);
        }
    }

    [Fact]
    public async Task Directory_quota_unsupported_kind_and_cancellation_are_fail_closed()
    {
        using var workspace = new TestWorkspace();
        var paths = PrepareEmpty(workspace);
        var store = Store(workspace, paths);
        for (var index = 0; index <= 256; index++)
        {
            var digest = Convert.ToHexString(SHA256.HashData(BitConverter.GetBytes(index))).ToLowerInvariant();
            Directory.CreateDirectory(Path.Combine(paths.CapabilityArtifactsPath, "staged", digest));
        }
        var stage = CapabilityArtifactStoreTestData.Stage(_versionOne);
        Assert.Equal(CapabilityLifecycleTargetResolutionStatus.Unavailable, (await store.ResolveAsync(Request(stage))).Status);
        Assert.Equal(CapabilityLifecycleTargetResolutionStatus.Unavailable, (await store.ResolveAsync(new CapabilityLifecycleTargetResolutionRequest(CapabilityLifecycleOperationKind.Disable, stage.Manifest.Descriptor.Id))).Status);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAsync<OperationCanceledException>(() => store.ResolveAsync(Request(stage), cancellation.Token));
    }

    [Fact]
    public async Task Windows_ancestor_reparse_swap_cannot_redirect_retained_parent_resolution()
    {
        using var workspace = new TestWorkspace();
        var paths = new WorkspacePaths(workspace.RootPath);
        var first = CapabilityArtifactStoreTestData.Stage(_versionOne);
        var second = CapabilityArtifactStoreTestData.Stage(_versionTwo, "2.0.0");
        var stagingStore = Store(workspace, paths);
        Assert.Equal(CapabilityArtifactStoreStatus.Applied, (await stagingStore.StageAsync(first)).Status);
        Assert.Equal(CapabilityArtifactStoreStatus.Applied, (await stagingStore.StageAsync(second)).Status);
        var normal = await stagingStore.ResolveAsync(Request(first) with { TargetVersion = first.Manifest.Descriptor.Version });
        Assert.Equal(CapabilityLifecycleTargetResolutionStatus.Available, normal.Status);
        Assert.Equal(first.Manifest.Checksum, normal.ArtifactDigest);

        var attackerArtifacts = Path.Combine(workspace.RootPath, "attacker-artifacts");
        CopyDirectory(paths.CapabilityArtifactsPath, attackerArtifacts);
        Directory.Delete(StagedRoot(paths.CapabilityArtifactsPath, second), recursive: true);
        Directory.Delete(StagedRoot(attackerArtifacts, first), recursive: true);
        var retainedArtifacts = Path.Combine(paths.CapabilityCatalogPath, "retained-artifacts");
        var observer = new SwappingCapabilityCatalogPathObserver(paths.CapabilityArtifactsPath, retainedArtifacts, attackerArtifacts);
        var resolver = Store(workspace, paths, observer);

        var result = await resolver.ResolveAsync(Request(first));

        Assert.True(observer.Attempted);
        if (observer.Swapped)
        {
            Assert.Equal(CapabilityLifecycleTargetResolutionStatus.Available, result.Status);
            Assert.Equal(first.Manifest.Descriptor.Version, result.Descriptor!.Version);
            Assert.Equal(first.Manifest.Checksum, result.ArtifactDigest);
        }
        else
        {
            Assert.NotNull(observer.RejectedByOperatingSystem);
            Assert.Equal(CapabilityLifecycleTargetResolutionStatus.Unavailable, result.Status);
        }
    }

    private static CapabilityLifecycleTargetResolutionRequest Request(CapabilityArtifactStageRequest stage) => new(CapabilityLifecycleOperationKind.Enable, stage.Manifest.Descriptor.Id);

    private static CapabilityArtifactStore Store(TestWorkspace workspace, WorkspacePaths paths, ICapabilityCatalogPathObserver? pathObserver = null) => new(paths, new FileCapabilityArtifactStateTrustProvider(workspace.ServerStatePath), new TestCapabilityArtifactTrustVerifier(), pathObserver: pathObserver);

    private static WorkspacePaths PrepareEmpty(TestWorkspace workspace)
    {
        var paths = new WorkspacePaths(workspace.RootPath);
        Directory.CreateDirectory(Path.Combine(paths.CapabilityArtifactsPath, "staged"));
        return paths;
    }

    private static string EvidencePath(WorkspacePaths paths, CapabilityArtifactStageRequest stage) => Path.Combine(paths.CapabilityArtifactsPath, "staged", stage.Manifest.Checksum.Value["sha256:".Length..], "artifact.evidence.json");

    private static string ContentPath(WorkspacePaths paths, CapabilityArtifactStageRequest stage) => Path.Combine(paths.CapabilityArtifactsPath, "staged", stage.Manifest.Checksum.Value["sha256:".Length..], stage.Manifest.EntryPoint);

    private static string StagedRoot(string artifactsPath, CapabilityArtifactStageRequest stage) => Path.Combine(artifactsPath, "staged", stage.Manifest.Checksum.Value["sha256:".Length..]);

    private static void CopyDirectory(string source, string destination)
    {
        Directory.CreateDirectory(destination);
        foreach (var directory in Directory.EnumerateDirectories(source, "*", SearchOption.AllDirectories))
        {
            Directory.CreateDirectory(Path.Combine(destination, Path.GetRelativePath(source, directory)));
        }
        foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
        {
            File.Copy(file, Path.Combine(destination, Path.GetRelativePath(source, file)));
        }
    }

    [DllImport("libc", EntryPoint = "link", SetLastError = true)]
    private static extern int CreateHardLinkUnix(string existingPath, string newPath);

    [DllImport("libc", EntryPoint = "mkfifo", SetLastError = true)]
    private static extern int CreateFifoUnix(string path, int mode);

    private sealed class CancelingCapabilityCatalogPathObserver(CancellationTokenSource cancellation) : ICapabilityCatalogPathObserver
    {
        private int _canceled;

        public void BeforeDirectoryChildOpen(string parentPath, string childName)
        {
            _ = parentPath;
            _ = childName;
            if (Interlocked.Exchange(ref _canceled, 1) == 0)
            {
                cancellation.Cancel();
            }
        }

        public void BeforeFileChildOpen(string parentPath, string childName)
        {
            _ = parentPath;
            _ = childName;
        }

        public void AfterFileChildOpen(string parentPath, string childName)
        {
            _ = parentPath;
            _ = childName;
        }
    }
}
