using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using EmbodySense.Core.Common.Governance.Tools;
using EmbodySense.Core.Common.Governance.Tools.Models;
using EmbodySense.Core.Common.Loops.Models;
using EmbodySense.Core.Common.Workspace;
using EmbodySense.Core.Persistence.ToolResults;
using EmbodySense.Tests.Support;

namespace EmbodySense.Core.Persistence.Tests.ToolResults;

public sealed class ToolResultRetentionStoreTests
{
    [Fact]
    public async Task RetainAsync_preserves_the_exact_full_response_in_agent_readable_chunks()
    {
        using var workspace = new TestWorkspace();
        var paths = new WorkspacePaths(workspace.RootPath);
        var output = new string('a', ToolResultRetentionLimits.MaxChunkCharacters - 1) + "\U0001F600" + new string('z', 40_000);
        var result = Result("00000000000000000000000000000001", output, Correlation());

        var reference = await new ToolResultRetentionStore(paths).RetainAsync(result, LoopDefinition.CreateDefaultConversation());

        Assert.Equal(ToolResultRetentionStatus.Retained, reference.Status);
        Assert.Equal(output.Length, reference.CharacterCount);
        Assert.Equal(Encoding.UTF8.GetByteCount(output), reference.Utf8ByteCount);
        Assert.Equal(3, reference.ChunkCount);
        Assert.Equal(Sha256(output), reference.ContentSha256);
        var manifestPath = workspace.File(reference.ManifestPath!.Replace('/', Path.DirectorySeparatorChar));
        using var manifest = JsonDocument.Parse(await File.ReadAllTextAsync(manifestPath));
        var root = manifest.RootElement;
        Assert.Equal("run-1", root.GetProperty("runId").GetString());
        Assert.Equal("step-1", root.GetProperty("stepId").GetString());
        Assert.Equal("read", root.GetProperty("command").GetString());
        Assert.Equal("shared/note.txt", root.GetProperty("targetPath").GetString());
        Assert.Equal("succeeded", root.GetProperty("outcome").GetString());
        var artifactDirectory = Path.GetDirectoryName(manifestPath)!;
        var chunks = root.GetProperty("chunks").EnumerateArray()
            .Select(chunk => File.ReadAllText(Path.Combine(artifactDirectory, chunk.GetProperty("path").GetString()!)))
            .ToArray();
        Assert.Equal(output, string.Concat(chunks));
        Assert.All(chunks, chunk => Assert.True(chunk.Length <= ToolResultRetentionLimits.MaxChunkCharacters));
        _ = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true).GetBytes(string.Concat(chunks));
    }

    [Fact]
    public async Task RetainAsync_accepts_the_sixth_utf16_safe_chunk_at_the_maximum_character_bound()
    {
        using var workspace = new TestWorkspace();
        var prefix = new string('a', ToolResultRetentionLimits.MaxChunkCharacters - 1) + "\U0001F600";
        var output = prefix + new string('z', ToolResultRetentionLimits.MaxOutputCharacters - prefix.Length);
        var result = Result("00000000000000000000000000000002", output);
        var store = new ToolResultRetentionStore(new WorkspacePaths(workspace.RootPath));

        var retained = await store.RetainAsync(result, LoopDefinition.CreateDefaultConversation());
        var repeated = await store.RetainAsync(result, LoopDefinition.CreateDefaultConversation());

        Assert.Equal(ToolResultRetentionStatus.Retained, retained.Status);
        Assert.Equal(6, retained.ChunkCount);
        Assert.Equal(ToolResultRetentionStatus.Retained, repeated.Status);
        Assert.Equal(retained.ContentSha256, repeated.ContentSha256);
    }

    [Fact]
    public async Task RetainAsync_evicts_only_the_oldest_complete_artifact_at_the_count_limit()
    {
        using var workspace = new TestWorkspace();
        var paths = new WorkspacePaths(workspace.RootPath);
        var future = new DateTimeOffset(2035, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var store = new ToolResultRetentionStore(paths, new FixedTimeProvider(future));
        for (var index = 0; index < ToolResultRetentionLimits.MaxArtifactsPerWorkspace; index++)
        {
            var retained = await store.RetainAsync(Result(index.ToString("x32"), $"result-{index}"), LoopDefinition.CreateDefaultConversation());
            Assert.Equal(ToolResultRetentionStatus.Retained, retained.Status);
            Assert.Equal(0, retained.EvictedArtifactCount);
        }

        var latest = await new ToolResultRetentionStore(paths, new FixedTimeProvider(future.AddYears(-10)))
            .RetainAsync(Result(new string('f', 32), "newest"), LoopDefinition.CreateDefaultConversation());

        Assert.Equal(ToolResultRetentionStatus.Retained, latest.Status);
        Assert.Equal(1, latest.EvictedArtifactCount);
        Assert.Equal(future.AddTicks(ToolResultRetentionLimits.MaxArtifactsPerWorkspace), latest.RetainedAtUtc);
        Assert.False(Directory.Exists(Path.Combine(paths.ToolResponsesPath, new string('0', 32))));
        Assert.True(Directory.Exists(Path.Combine(paths.ToolResponsesPath, new string('f', 32))));
        Assert.Equal(ToolResultRetentionLimits.MaxArtifactsPerWorkspace, Directory.EnumerateDirectories(paths.ToolResponsesPath).Count());
    }

    [Fact]
    public async Task RetainAsync_cleans_an_orphaned_exact_staging_directory_under_the_workspace_lease()
    {
        using var workspace = new TestWorkspace();
        var paths = new WorkspacePaths(workspace.RootPath);
        var orphan = Path.Combine(paths.ToolResponsesPath, ".staging-" + new string('a', 32));
        Directory.CreateDirectory(orphan);
        await File.WriteAllTextAsync(Path.Combine(orphan, "partial.txt"), "partial");

        var retained = await new ToolResultRetentionStore(paths).RetainAsync(Result(new string('1', 32), "complete"), LoopDefinition.CreateDefaultConversation());

        Assert.Equal(ToolResultRetentionStatus.Retained, retained.Status);
        Assert.False(Directory.Exists(orphan));
    }

    [Fact]
    public async Task RetainAsync_fails_closed_without_advertising_partial_evidence_for_unrecognized_or_oversize_content()
    {
        using var workspace = new TestWorkspace();
        using var oversizeWorkspace = new TestWorkspace();
        var paths = new WorkspacePaths(workspace.RootPath);
        Directory.CreateDirectory(Path.Combine(paths.ToolResponsesPath, "user-content"));
        var store = new ToolResultRetentionStore(paths);

        var unrecognized = await store.RetainAsync(Result(new string('2', 32), "response"), LoopDefinition.CreateDefaultConversation());
        var oversize = await new ToolResultRetentionStore(new WorkspacePaths(oversizeWorkspace.RootPath)).RetainAsync(
            Result(new string('3', 32), new string('x', ToolResultRetentionLimits.MaxOutputCharacters + 1)),
            LoopDefinition.CreateDefaultConversation());

        Assert.Equal(ToolResultRetentionStatus.Unavailable, unrecognized.Status);
        Assert.Null(unrecognized.ManifestPath);
        Assert.Contains("InvalidDataException", unrecognized.Detail, StringComparison.Ordinal);
        Assert.Equal(ToolResultRetentionStatus.Unavailable, oversize.Status);
        Assert.Null(oversize.ManifestPath);
        Assert.Contains("InvalidDataException", oversize.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RetainAsync_serializes_concurrent_writers_across_store_instances()
    {
        using var workspace = new TestWorkspace();
        var paths = new WorkspacePaths(workspace.RootPath);
        var first = new ToolResultRetentionStore(paths);
        var second = new ToolResultRetentionStore(paths);

        var retained = await Task.WhenAll(
            first.RetainAsync(Result(new string('4', 32), new string('a', 60_000)), LoopDefinition.CreateDefaultConversation()),
            second.RetainAsync(Result(new string('5', 32), new string('b', 60_000)), LoopDefinition.CreateDefaultConversation()));

        Assert.All(retained, reference => Assert.Equal(ToolResultRetentionStatus.Retained, reference.Status));
        Assert.Equal(2, Directory.EnumerateDirectories(paths.ToolResponsesPath).Count());
    }

    [Fact]
    public async Task RetainAsync_reuses_matching_request_evidence_and_refuses_conflicting_request_identity()
    {
        using var workspace = new TestWorkspace();
        var store = new ToolResultRetentionStore(new WorkspacePaths(workspace.RootPath));
        var requestId = new string('6', 32);
        var original = Result(requestId, "original");

        var first = await store.RetainAsync(original, LoopDefinition.CreateDefaultConversation());
        var repeated = await store.RetainAsync(original, LoopDefinition.CreateDefaultConversation());
        var conflicting = await store.RetainAsync(Result(requestId, "different"), LoopDefinition.CreateDefaultConversation());

        Assert.Equal(ToolResultRetentionStatus.Retained, first.Status);
        Assert.Equal(first.ManifestPath, repeated.ManifestPath);
        Assert.Equal(first.ContentSha256, repeated.ContentSha256);
        Assert.Equal(ToolResultRetentionStatus.Unavailable, conflicting.Status);
        Assert.Contains("already bound", conflicting.Detail, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("staging")]
    [InlineData("root-file")]
    [InlineData("manifest")]
    [InlineData("chunk-content")]
    [InlineData("aggregate-hash")]
    [InlineData("oversized-manifest")]
    public async Task RetainAsync_fails_closed_when_existing_retention_state_is_not_canonical(string corruption)
    {
        using var workspace = new TestWorkspace();
        var paths = new WorkspacePaths(workspace.RootPath);
        var store = new ToolResultRetentionStore(paths);
        Directory.CreateDirectory(paths.ToolResponsesPath);

        if (corruption == "staging")
        {
            Directory.CreateDirectory(Path.Combine(paths.ToolResponsesPath, ".staging-not-a-request-id"));
        }
        else if (corruption == "root-file")
        {
            await File.WriteAllTextAsync(Path.Combine(paths.ToolResponsesPath, "unexpected.txt"), "unexpected");
        }
        else
        {
            var retained = await store.RetainAsync(Result(new string('7', 32), "original"), LoopDefinition.CreateDefaultConversation());
            var manifestPath = workspace.File(retained.ManifestPath!.Replace('/', Path.DirectorySeparatorChar));
            if (corruption == "manifest")
            {
                var manifest = await File.ReadAllTextAsync(manifestPath);
                await File.WriteAllTextAsync(manifestPath, manifest.Replace("\"schemaVersion\": 1", "\"schemaVersion\": 2", StringComparison.Ordinal));
            }
            else if (corruption == "chunk-content")
            {
                var chunkPath = Path.Combine(Path.GetDirectoryName(manifestPath)!, "0001.txt");
                var content = await File.ReadAllTextAsync(chunkPath);
                await File.WriteAllTextAsync(chunkPath, new string('x', content.Length));
            }
            else if (corruption == "aggregate-hash")
            {
                var manifest = JsonNode.Parse(await File.ReadAllTextAsync(manifestPath))!.AsObject();
                manifest["contentSha256"] = new string('0', 64);
                await File.WriteAllTextAsync(manifestPath, manifest.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
            }
            else
            {
                await File.WriteAllTextAsync(manifestPath, new string('x', ToolResultRetentionLimits.MaxManifestUtf8Bytes + 1));
            }
        }

        var unavailable = await store.RetainAsync(Result(new string('8', 32), "next"), LoopDefinition.CreateDefaultConversation());

        Assert.Equal(ToolResultRetentionStatus.Unavailable, unavailable.Status);
        Assert.Contains("InvalidDataException", unavailable.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RetainAsync_refuses_a_response_root_that_redirects_outside_the_workspace()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        using var workspace = new TestWorkspace();
        using var outside = new TestWorkspace();
        var paths = new WorkspacePaths(workspace.RootPath);
        Directory.CreateDirectory(paths.LogsPath);
        Directory.CreateSymbolicLink(paths.ToolResponsesPath, outside.RootPath);

        var retained = await new ToolResultRetentionStore(paths).RetainAsync(Result(new string('6', 32), "sensitive"), LoopDefinition.CreateDefaultConversation());

        Assert.Equal(ToolResultRetentionStatus.Unavailable, retained.Status);
        Assert.Contains("InvalidDataException", retained.Detail, StringComparison.Ordinal);
        Assert.Empty(Directory.EnumerateFileSystemEntries(outside.RootPath));
    }

    [Fact]
    public async Task RetainAsync_refuses_a_workspace_root_that_redirects_to_another_location()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        using var linkHost = new TestWorkspace();
        using var outside = new TestWorkspace();
        var workspaceLink = linkHost.File("workspace-link");
        Directory.CreateSymbolicLink(workspaceLink, outside.RootPath);
        try
        {
            var retained = await new ToolResultRetentionStore(new WorkspacePaths(workspaceLink))
                .RetainAsync(Result(new string('9', 32), "sensitive"), LoopDefinition.CreateDefaultConversation());

            Assert.Equal(ToolResultRetentionStatus.Unavailable, retained.Status);
            Assert.Contains("InvalidDataException", retained.Detail, StringComparison.Ordinal);
            Assert.Empty(Directory.EnumerateFileSystemEntries(outside.RootPath));
        }
        finally
        {
            Directory.Delete(workspaceLink);
        }
    }

    private static ToolResult Result(string requestId, string output, ToolAuditCorrelation? correlation = null)
    {
        return new ToolResult(
            ToolExecutionOutcome.Succeeded,
            output,
            requestId,
            "C:\\workspace\\shared\\note.txt",
            new ToolRequest(ToolCommand.Read, "shared/note.txt", CorrelationId: "provider-call-1", AuditCorrelation: correlation));
    }

    private static ToolAuditCorrelation Correlation()
    {
        return new ToolAuditCorrelation("run-1", "loop-1", "role-1", 2, new string('a', 64), 3, "step-1", 1, "attempt-1");
    }

    private static string Sha256(string text)
    {
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text))).ToLowerInvariant();
    }

    private sealed class FixedTimeProvider(DateTimeOffset timestamp) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => timestamp;
    }
}
