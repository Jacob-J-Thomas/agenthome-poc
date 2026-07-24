using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using EmbodySense.Core.Application.Governance.Tools;
using EmbodySense.Core.Common.Governance.Tools;
using EmbodySense.Core.Common.Governance.Tools.Models;
using EmbodySense.Core.Common.Loops.Models;
using EmbodySense.Core.Common.Workspace;
using EmbodySense.Core.Persistence.ToolResults.Models;

namespace EmbodySense.Core.Persistence.ToolResults;

public sealed class ToolResultRetentionStore : IToolResultRetentionStore
{
    private const int CurrentSchemaVersion = 1;
    private const string ManifestFileName = "manifest.json";
    private const string StagingPrefix = ".staging-";
    private const string RetentionPolicy = "oldest-first within 256 artifacts and 64 MiB; full response chunks are sensitive local workspace evidence";
    private static readonly TimeSpan LockRetryDelay = TimeSpan.FromMilliseconds(25);
    private static readonly UTF8Encoding StrictUtf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);
    private static readonly JsonSerializerOptions JsonOptions = CreateJsonOptions();
    private readonly WorkspacePaths _paths;

    public ToolResultRetentionStore(WorkspacePaths paths)
    {
        ArgumentNullException.ThrowIfNull(paths);
        _paths = paths;
    }

    public async Task<ToolResultRetentionReference> RetainAsync(ToolResult result, LoopDefinition loopDefinition, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(result);
        ArgumentNullException.ThrowIfNull(loopDefinition);

        try
        {
            ValidateResult(result);
            PrepareRetentionRoot();
            await using var lease = await AcquireLeaseAsync(cancellationToken);
            CleanupStagingDirectories();
            var existingArtifacts = await LoadArtifactsAsync(cancellationToken);
            var recoveredEvictions = EvictToLimits(existingArtifacts);
            var retainedAtUtc = DateTimeOffset.UtcNow;
            var prepared = PrepareArtifact(result, loopDefinition, retainedAtUtc);
            var existing = existingArtifacts.SingleOrDefault(artifact => !artifact.Evicted && string.Equals(artifact.Manifest.RequestId, result.RequestId, StringComparison.Ordinal));
            if (existing is not null)
            {
                return SameArtifact(existing.Manifest, prepared.Manifest)
                    ? CreateReference(existing.Manifest, recoveredEvictions)
                    : Unavailable(result, "The broker request id is already bound to different retained response evidence.");
            }

            if (prepared.TotalUtf8Bytes > ToolResultRetentionLimits.MaxArtifactUtf8Bytes)
            {
                return Unavailable(result, $"The complete governed response exceeds the {ToolResultRetentionLimits.MaxArtifactUtf8Bytes}-byte artifact limit.");
            }

            await WriteArtifactAsync(prepared, cancellationToken);
            existingArtifacts.Add(new RetainedArtifact(Path.Combine(_paths.ToolResponsesPath, prepared.Manifest.RequestId), prepared.Manifest, prepared.TotalUtf8Bytes));
            var evicted = recoveredEvictions + EvictToLimits(existingArtifacts);
            return CreateReference(prepared.Manifest, evicted);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            return Unavailable(result, $"Durable full-response retention failed closed with {exception.GetType().Name}; no partial response artifact is advertised.");
        }
    }

    private async Task<FileStream> AcquireLeaseAsync(CancellationToken cancellationToken)
    {
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                return new FileStream(_paths.ToolResponseRetentionLockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None, 1, FileOptions.WriteThrough);
            }
            catch (IOException)
            {
                await Task.Delay(LockRetryDelay, cancellationToken);
            }
        }
    }

    private void PrepareRetentionRoot()
    {
        foreach (var directory in new[] { _paths.AgentPath, _paths.LogsPath, _paths.ToolResponsesPath })
        {
            if (!Directory.Exists(directory))
            {
                Directory.CreateDirectory(directory);
            }

            EnsurePlainDirectory(directory);
        }

        if (File.Exists(_paths.ToolResponseRetentionLockPath) && File.GetAttributes(_paths.ToolResponseRetentionLockPath).HasFlag(FileAttributes.ReparsePoint))
        {
            throw new InvalidDataException("The tool-response retention lock cannot be a reparse point.");
        }
    }

    private void CleanupStagingDirectories()
    {
        foreach (var directory in Directory.EnumerateDirectories(_paths.ToolResponsesPath, StagingPrefix + "*", SearchOption.TopDirectoryOnly))
        {
            var stagingId = Path.GetFileName(directory)[StagingPrefix.Length..];
            if (!IsRequestId(stagingId))
            {
                throw new InvalidDataException("The tool-response retention root contains an unrecognized staging directory.");
            }

            EnsurePlainDirectory(directory);
            Directory.Delete(directory, recursive: true);
        }
    }

    private async Task<List<RetainedArtifact>> LoadArtifactsAsync(CancellationToken cancellationToken)
    {
        var allowedRootFile = Path.GetFullPath(_paths.ToolResponseRetentionLockPath);
        foreach (var file in Directory.EnumerateFiles(_paths.ToolResponsesPath, "*", SearchOption.TopDirectoryOnly))
        {
            if (!PathEquals(file, allowedRootFile))
            {
                throw new InvalidDataException("The tool-response retention root contains an unrecognized file.");
            }
        }

        var artifacts = new List<RetainedArtifact>();
        foreach (var directory in Directory.EnumerateDirectories(_paths.ToolResponsesPath, "*", SearchOption.TopDirectoryOnly))
        {
            EnsurePlainDirectory(directory);
            var requestId = Path.GetFileName(directory);
            if (!IsRequestId(requestId))
            {
                throw new InvalidDataException("The tool-response retention root contains an unrecognized directory.");
            }

            var manifestPath = Path.Combine(directory, ManifestFileName);
            var manifest = JsonSerializer.Deserialize<ToolResultArtifactManifest>(await File.ReadAllTextAsync(manifestPath, cancellationToken), JsonOptions)
                ?? throw new InvalidDataException("A retained tool-response manifest is empty.");
            var totalUtf8Bytes = ValidateArtifact(directory, requestId, manifest);
            artifacts.Add(new RetainedArtifact(directory, manifest, totalUtf8Bytes));
        }

        return artifacts;
    }

    private static long ValidateArtifact(string directory, string requestId, ToolResultArtifactManifest manifest)
    {
        if (manifest.SchemaVersion != CurrentSchemaVersion || !string.Equals(manifest.RequestId, requestId, StringComparison.Ordinal) || !IsSha256(manifest.ContentSha256))
        {
            throw new InvalidDataException("A retained tool-response manifest has an incompatible identity or schema.");
        }

        if (manifest.CharacterCount < 0 || manifest.CharacterCount > ToolResultRetentionLimits.MaxOutputCharacters || manifest.Utf8ByteCount < 0 || manifest.Chunks.Length == 0)
        {
            throw new InvalidDataException("A retained tool-response manifest exceeds its governed content bounds.");
        }

        var expectedFiles = new HashSet<string>(StringComparer.Ordinal) { ManifestFileName };
        var manifestFile = new FileInfo(Path.Combine(directory, ManifestFileName));
        if (!manifestFile.Exists || manifestFile.Attributes.HasFlag(FileAttributes.ReparsePoint))
        {
            throw new InvalidDataException("A retained tool-response manifest is missing or is a reparse point.");
        }

        long totalUtf8Bytes = manifestFile.Length;
        long chunkUtf8Bytes = 0;
        var chunkCharacters = 0;
        for (var index = 0; index < manifest.Chunks.Length; index++)
        {
            var chunk = manifest.Chunks[index];
            var expectedPath = ChunkFileName(index + 1);
            if (chunk.Sequence != index + 1 || !string.Equals(chunk.Path, expectedPath, StringComparison.Ordinal) || !IsSha256(chunk.ContentSha256))
            {
                throw new InvalidDataException("A retained tool-response chunk manifest is not canonical.");
            }

            var chunkPath = Path.Combine(directory, expectedPath);
            var file = new FileInfo(chunkPath);
            if (!file.Exists || file.Attributes.HasFlag(FileAttributes.ReparsePoint) || file.Length != chunk.Utf8ByteCount || chunk.CharacterCount < 0 || chunk.CharacterCount > ToolResultRetentionLimits.MaxChunkCharacters)
            {
                throw new InvalidDataException("A retained tool-response chunk does not match its manifest bounds.");
            }

            expectedFiles.Add(expectedPath);
            totalUtf8Bytes += file.Length;
            chunkUtf8Bytes += chunk.Utf8ByteCount;
            chunkCharacters += chunk.CharacterCount;
        }

        foreach (var file in Directory.EnumerateFiles(directory, "*", SearchOption.TopDirectoryOnly))
        {
            if (!expectedFiles.Contains(Path.GetFileName(file)))
            {
                throw new InvalidDataException("A retained tool-response artifact contains an unrecognized file.");
            }
        }

        if (Directory.EnumerateDirectories(directory, "*", SearchOption.TopDirectoryOnly).Any() || chunkUtf8Bytes != manifest.Utf8ByteCount || chunkCharacters != manifest.CharacterCount || totalUtf8Bytes > ToolResultRetentionLimits.MaxArtifactUtf8Bytes)
        {
            throw new InvalidDataException("A retained tool-response artifact does not match its manifest totals.");
        }

        return totalUtf8Bytes;
    }

    private PreparedArtifact PrepareArtifact(ToolResult result, LoopDefinition loopDefinition, DateTimeOffset retainedAtUtc)
    {
        var chunks = new List<PreparedChunk>();
        var offset = 0;
        var sequence = 1;
        while (offset < result.OutputText.Length || chunks.Count == 0)
        {
            var characterCount = Math.Min(ToolResultRetentionLimits.MaxChunkCharacters, result.OutputText.Length - offset);
            if (characterCount > 0 && offset + characterCount < result.OutputText.Length && char.IsHighSurrogate(result.OutputText[offset + characterCount - 1]) && char.IsLowSurrogate(result.OutputText[offset + characterCount]))
            {
                characterCount--;
            }

            var content = result.OutputText.Substring(offset, characterCount);
            var bytes = StrictUtf8.GetBytes(content);
            var path = ChunkFileName(sequence);
            chunks.Add(new PreparedChunk(path, bytes, new ToolResultArtifactChunk(sequence, path, Sha256(bytes), content.Length, bytes.LongLength)));
            offset += characterCount;
            sequence++;
        }

        var correlation = result.Request.AuditCorrelation;
        var contentBytes = StrictUtf8.GetBytes(result.OutputText);
        var manifest = new ToolResultArtifactManifest(
            CurrentSchemaVersion,
            result.RequestId,
            result.Request.CorrelationId,
            correlation?.LoopId ?? loopDefinition.Id,
            correlation?.RoleId ?? loopDefinition.RoleId,
            correlation?.RunId,
            correlation?.DefinitionVersion,
            correlation?.DefinitionHash,
            correlation?.Iteration,
            correlation?.StepId,
            correlation?.Attempt,
            correlation?.AttemptCorrelationId,
            result.Request.Command,
            result.Request.TargetPath,
            result.ResolvedPath,
            result.Outcome,
            Sha256(contentBytes),
            result.OutputText.Length,
            contentBytes.LongLength,
            retainedAtUtc,
            RetentionPolicy,
            chunks.Select(chunk => chunk.Manifest).ToArray());
        var manifestBytes = JsonSerializer.SerializeToUtf8Bytes(manifest, JsonOptions);
        return new PreparedArtifact(manifest, manifestBytes, chunks, manifestBytes.LongLength + chunks.Sum(chunk => chunk.Bytes.LongLength));
    }

    private int EvictToLimits(List<RetainedArtifact> artifacts)
    {
        var retainedCount = artifacts.Count(artifact => !artifact.Evicted);
        var retainedBytes = artifacts.Where(artifact => !artifact.Evicted).Sum(artifact => artifact.TotalUtf8Bytes);
        var evictedCount = 0;
        foreach (var artifact in artifacts.OrderBy(artifact => artifact.Manifest.RetainedAtUtc).ThenBy(artifact => artifact.Manifest.RequestId, StringComparer.Ordinal))
        {
            if (retainedCount <= ToolResultRetentionLimits.MaxArtifactsPerWorkspace && retainedBytes <= ToolResultRetentionLimits.MaxWorkspaceUtf8Bytes)
            {
                break;
            }

            if (artifact.Evicted)
            {
                continue;
            }

            Directory.Delete(artifact.Directory, recursive: true);
            artifact.Evicted = true;
            retainedCount--;
            retainedBytes -= artifact.TotalUtf8Bytes;
            evictedCount++;
        }

        if (retainedCount > ToolResultRetentionLimits.MaxArtifactsPerWorkspace || retainedBytes > ToolResultRetentionLimits.MaxWorkspaceUtf8Bytes)
        {
            throw new InvalidDataException("Retained tool-response evidence cannot be reduced to its governed workspace limits.");
        }

        return evictedCount;
    }

    private async Task WriteArtifactAsync(PreparedArtifact artifact, CancellationToken cancellationToken)
    {
        var stagingPath = Path.Combine(_paths.ToolResponsesPath, StagingPrefix + Guid.NewGuid().ToString("N"));
        var finalPath = Path.Combine(_paths.ToolResponsesPath, artifact.Manifest.RequestId);
        Directory.CreateDirectory(stagingPath);
        try
        {
            foreach (var chunk in artifact.Chunks)
            {
                await File.WriteAllBytesAsync(Path.Combine(stagingPath, chunk.Path), chunk.Bytes, cancellationToken);
            }

            await File.WriteAllBytesAsync(Path.Combine(stagingPath, ManifestFileName), artifact.ManifestBytes, cancellationToken);
            Directory.Move(stagingPath, finalPath);
        }
        finally
        {
            if (Directory.Exists(stagingPath))
            {
                Directory.Delete(stagingPath, recursive: true);
            }
        }
    }

    private ToolResultRetentionReference CreateReference(ToolResultArtifactManifest manifest, int evictedArtifactCount)
    {
        var manifestPath = Path.GetRelativePath(_paths.RootPath, Path.Combine(_paths.ToolResponsesPath, manifest.RequestId, ManifestFileName)).Replace(Path.DirectorySeparatorChar, '/');
        var detail = evictedArtifactCount == 0
            ? $"Retained under the {RetentionPolicy}."
            : $"Retained under the {RetentionPolicy}; evicted {evictedArtifactCount} oldest artifact(s) before this write.";
        return new ToolResultRetentionReference(
            ToolResultRetentionStatus.Retained,
            manifestPath,
            manifest.ContentSha256,
            manifest.CharacterCount,
            manifest.Utf8ByteCount,
            manifest.Chunks.Length,
            manifest.RetainedAtUtc,
            evictedArtifactCount,
            detail);
    }

    private static ToolResultRetentionReference Unavailable(ToolResult result, string detail)
    {
        return new ToolResultRetentionReference(ToolResultRetentionStatus.Unavailable, null, null, result.OutputText.Length, null, null, null, 0, detail);
    }

    private static bool SameArtifact(ToolResultArtifactManifest left, ToolResultArtifactManifest right)
    {
        return string.Equals(left.ContentSha256, right.ContentSha256, StringComparison.Ordinal)
            && left.CharacterCount == right.CharacterCount
            && left.Utf8ByteCount == right.Utf8ByteCount
            && left.Outcome == right.Outcome
            && left.Command == right.Command
            && string.Equals(left.TargetPath, right.TargetPath, StringComparison.Ordinal)
            && string.Equals(left.ResolvedPath, right.ResolvedPath, StringComparison.Ordinal);
    }

    private static void ValidateResult(ToolResult result)
    {
        if (!IsRequestId(result.RequestId))
        {
            throw new InvalidDataException("A retained tool response requires the broker's canonical 32-character request id.");
        }

        if (result.OutputText.Length > ToolResultRetentionLimits.MaxOutputCharacters)
        {
            throw new InvalidDataException($"The complete governed response exceeds the {ToolResultRetentionLimits.MaxOutputCharacters}-character retention limit.");
        }
    }

    private static void EnsurePlainDirectory(string path)
    {
        if (new DirectoryInfo(path).Attributes.HasFlag(FileAttributes.ReparsePoint))
        {
            throw new InvalidDataException("Retained tool-response directories cannot be reparse points.");
        }
    }

    private static bool IsRequestId(string value) => value.Length == 32 && value.All(character => character is >= '0' and <= '9' or >= 'a' and <= 'f');

    private static bool IsSha256(string value) => value.Length == 64 && value.All(character => character is >= '0' and <= '9' or >= 'a' and <= 'f');

    private static string ChunkFileName(int sequence) => $"{sequence:D4}.txt";

    private static string Sha256(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

    private static bool PathEquals(string left, string right) => string.Equals(Path.GetFullPath(left), Path.GetFullPath(right), OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);

    private static JsonSerializerOptions CreateJsonOptions()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web) { WriteIndented = true };
        options.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.CamelCase));
        return options;
    }

    private sealed record PreparedChunk(string Path, byte[] Bytes, ToolResultArtifactChunk Manifest);

    private sealed record PreparedArtifact(ToolResultArtifactManifest Manifest, byte[] ManifestBytes, IReadOnlyList<PreparedChunk> Chunks, long TotalUtf8Bytes);

    private sealed class RetainedArtifact(string directory, ToolResultArtifactManifest manifest, long totalUtf8Bytes)
    {
        public string Directory { get; } = directory;

        public ToolResultArtifactManifest Manifest { get; } = manifest;

        public long TotalUtf8Bytes { get; } = totalUtf8Bytes;

        public bool Evicted { get; set; }
    }
}
