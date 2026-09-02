using EmbodySense.Core.Common.Workspace;
using EmbodySense.Tests.Support;

namespace EmbodySense.E2ETests.Web;

public sealed class HumanReviewAuthorityParkingLeaseTests
{
    [Fact]
    public async Task Restore_cancellation_does_not_cache_partial_success()
    {
        using var workspace = new TestWorkspace();
        var paths = new WorkspacePaths(workspace.RootPath);
        var profileContent = "{\"profile\":\"original\"}"u8.ToArray();
        var proofContent = "{\"proof\":\"original\"}"u8.ToArray();
        Directory.CreateDirectory(paths.AuthorityProfilesPath);
        await File.WriteAllBytesAsync(paths.AuthorityProfilesDocumentPath, profileContent);
        await File.WriteAllBytesAsync(paths.AuthorityProfilesProofPath, proofContent);

        await using var lease = await HumanReviewAuthorityParkingLease.ParkAsync(paths);
        Assert.Equal("{", await File.ReadAllTextAsync(paths.AuthorityProfilesDocumentPath));
        Assert.Equal("{", await File.ReadAllTextAsync(paths.AuthorityProfilesProofPath));

        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => lease.RestoreAsync(cancellation.Token));
        Assert.Equal("{", await File.ReadAllTextAsync(paths.AuthorityProfilesDocumentPath));
        Assert.Equal("{", await File.ReadAllTextAsync(paths.AuthorityProfilesProofPath));

        await lease.RestoreAsync();
        Assert.Equal(profileContent, await File.ReadAllBytesAsync(paths.AuthorityProfilesDocumentPath));
        Assert.Equal(proofContent, await File.ReadAllBytesAsync(paths.AuthorityProfilesProofPath));
    }

    [Fact]
    public async Task Restore_partial_failure_after_first_artifact_remains_retryable()
    {
        using var workspace = new TestWorkspace();
        var paths = new WorkspacePaths(workspace.RootPath);
        var profileContent = "{\"profile\":\"original\"}"u8.ToArray();
        var proofContent = "{\"proof\":\"original\"}"u8.ToArray();
        Directory.CreateDirectory(paths.AuthorityProfilesPath);
        await File.WriteAllBytesAsync(paths.AuthorityProfilesDocumentPath, profileContent);
        await File.WriteAllBytesAsync(paths.AuthorityProfilesProofPath, proofContent);

        await using var lease = await HumanReviewAuthorityParkingLease.ParkAsync(paths);
        File.Delete(paths.AuthorityProfilesProofPath);
        Directory.CreateDirectory(paths.AuthorityProfilesProofPath);
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(500));
        try
        {
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => lease.RestoreAsync(cancellation.Token));
            Assert.Equal(profileContent, await File.ReadAllBytesAsync(paths.AuthorityProfilesDocumentPath));
            Assert.True(Directory.Exists(paths.AuthorityProfilesProofPath));
        }
        finally
        {
            if (Directory.Exists(paths.AuthorityProfilesProofPath))
                Directory.Delete(paths.AuthorityProfilesProofPath);
        }

        await lease.RestoreAsync();
        Assert.Equal(profileContent, await File.ReadAllBytesAsync(paths.AuthorityProfilesDocumentPath));
        Assert.Equal(proofContent, await File.ReadAllBytesAsync(paths.AuthorityProfilesProofPath));
    }
}
