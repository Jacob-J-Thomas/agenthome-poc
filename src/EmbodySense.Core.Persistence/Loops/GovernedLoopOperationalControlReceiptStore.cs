using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using EmbodySense.Core.Application.Loops.Posture;
using EmbodySense.Core.Application.Loops.Posture.Models;
using EmbodySense.Core.Common.Loops.Custom;
using EmbodySense.Core.Common.Loops.Posture;
using EmbodySense.Core.Common.Loops.Posture.Models;
using EmbodySense.Core.Common.Workspace;
using EmbodySense.Core.Persistence.Loops.Models;

namespace EmbodySense.Core.Persistence.Loops;

/// <summary>Persists bounded schema-1 operational-control receipts under cross-process ownership.</summary>
/// <remarks>Intent is flushed before a lease is returned. Exact pending replay can reclaim an abandoned owner lock after process restart; complete replay never reacquires mutation ownership.</remarks>
public sealed class GovernedLoopOperationalControlReceiptStore : IGovernedLoopOperationalControlReceiptStore
{
    private const int MaximumConfiguredReceipts = 16_384;
    private const int MaximumConfiguredReceiptBytes = 1024 * 1024;
    private readonly CustomLoopArtifactPathGuard _guard;
    private readonly int _maximumReceiptBytes;
    private readonly int _maximumReceipts;
    private readonly string _root;
    private static readonly JsonSerializerOptions _jsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = false,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        WriteIndented = true,
        MaxDepth = 8,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase, allowIntegerValues: false) }
    };

    /// <summary>Creates one workspace-scoped receipt store.</summary>
    public GovernedLoopOperationalControlReceiptStore(
        WorkspacePaths paths,
        GovernedLoopOperationalControlReceiptStoreOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(paths);
        options ??= new GovernedLoopOperationalControlReceiptStoreOptions();
        if (options.MaxReceipts is < 1 or > MaximumConfiguredReceipts)
        {
            throw new ArgumentOutOfRangeException(nameof(options), "Operational-control receipt count is outside supported bounds.");
        }
        if (options.MaxReceiptUtf8Bytes is < 1 or > MaximumConfiguredReceiptBytes)
        {
            throw new ArgumentOutOfRangeException(nameof(options), "Operational-control receipt bytes are outside supported bounds.");
        }
        _maximumReceipts = options.MaxReceipts;
        _maximumReceiptBytes = options.MaxReceiptUtf8Bytes;
        _root = paths.GovernedLoopOperationalControlReceiptsPath;
        _guard = new CustomLoopArtifactPathGuard(paths.RootPath);
    }

    /// <inheritdoc />
    public async Task<GovernedLoopOperationalControlReceiptStoreResult> BeginAsync(
        GovernedLoopOperationalControlReceipt receipt,
        CancellationToken cancellationToken = default)
    {
        if (!TryCapture(receipt, out var captured) || captured.State != GovernedLoopOperationalControlReceiptState.Pending)
        {
            return Result(GovernedLoopOperationalControlReceiptStoreStatus.Corrupt);
        }
        receipt = captured;

        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            using var mutation = _guard.AcquireExclusiveMutationLock(_root);
            ValidateDirectory();
            var path = ReceiptPath(receipt.OperationId);
            if (File.Exists(path))
            {
                var existing = await ReadAsync(path, cancellationToken).ConfigureAwait(false);
                if (!string.Equals(existing.RequestHash, receipt.RequestHash, StringComparison.Ordinal))
                {
                    return Result(GovernedLoopOperationalControlReceiptStoreStatus.Conflict, existing);
                }
                if (existing.State is GovernedLoopOperationalControlReceiptState.Complete or GovernedLoopOperationalControlReceiptState.NeedsReview)
                {
                    return Result(GovernedLoopOperationalControlReceiptStoreStatus.Replayed, existing);
                }
                var replayLease = TryAcquireOwner(receipt.OperationId);
                return replayLease is null
                    ? Result(GovernedLoopOperationalControlReceiptStoreStatus.OperationInProgress, existing)
                    : new GovernedLoopOperationalControlReceiptStoreResult(GovernedLoopOperationalControlReceiptStoreStatus.Replayed, existing, replayLease);
            }

            if (ReceiptPaths().Count >= _maximumReceipts)
            {
                return Result(GovernedLoopOperationalControlReceiptStoreStatus.Backpressured);
            }
            var lease = TryAcquireOwner(receipt.OperationId);
            if (lease is null)
            {
                return Result(GovernedLoopOperationalControlReceiptStoreStatus.OperationInProgress);
            }
            try
            {
                await WriteAsync(path, receipt, cancellationToken).ConfigureAwait(false);
                return new GovernedLoopOperationalControlReceiptStoreResult(GovernedLoopOperationalControlReceiptStoreStatus.Committed, Copy(receipt), lease);
            }
            catch
            {
                lease.Dispose();
                throw;
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception) when (IsCorrupt(exception))
        {
            return Result(GovernedLoopOperationalControlReceiptStoreStatus.Corrupt);
        }
        catch (Exception exception) when (IsUnavailable(exception))
        {
            return Result(GovernedLoopOperationalControlReceiptStoreStatus.Unavailable);
        }
    }

    /// <inheritdoc />
    public async Task<GovernedLoopOperationalControlReceiptStoreResult> CompareExchangeAsync(
        string expectedContentHash,
        GovernedLoopOperationalControlReceipt replacement,
        CancellationToken cancellationToken = default)
    {
        if (!GovernedLoopOperationalContract.IsHash(expectedContentHash) || !TryCapture(replacement, out var captured))
        {
            return Result(GovernedLoopOperationalControlReceiptStoreStatus.Corrupt);
        }
        replacement = captured;

        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            using var mutation = _guard.AcquireExclusiveMutationLock(_root);
            ValidateDirectory();
            var path = ReceiptPath(replacement.OperationId);
            if (!File.Exists(path))
            {
                return Result(GovernedLoopOperationalControlReceiptStoreStatus.Conflict);
            }
            var current = await ReadAsync(path, cancellationToken).ConfigureAwait(false);
            if (string.Equals(current.ContentHash, replacement.ContentHash, StringComparison.Ordinal))
            {
                return Result(GovernedLoopOperationalControlReceiptStoreStatus.Replayed, current);
            }
            if (!string.Equals(current.ContentHash, expectedContentHash, StringComparison.Ordinal)
                || !IsSuccessor(current, replacement))
            {
                return Result(GovernedLoopOperationalControlReceiptStoreStatus.Conflict, current);
            }
            await WriteAsync(path, replacement, cancellationToken).ConfigureAwait(false);
            return Result(GovernedLoopOperationalControlReceiptStoreStatus.Committed, replacement);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception) when (IsCorrupt(exception))
        {
            return Result(GovernedLoopOperationalControlReceiptStoreStatus.Corrupt);
        }
        catch (Exception exception) when (IsUnavailable(exception))
        {
            return Result(GovernedLoopOperationalControlReceiptStoreStatus.Unavailable);
        }
    }

    private GovernedLoopOperationalControlLease? TryAcquireOwner(string operationId)
    {
        var path = _guard.GetFilePath(_root, operationId + ".owner");
        try
        {
            var stream = new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None, 1, FileOptions.WriteThrough);
            return new GovernedLoopOperationalControlLease(stream);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private void ValidateDirectory()
    {
        var entries = Directory.EnumerateFileSystemEntries(_root).Take(checked(_maximumReceipts * 2 + 3)).ToArray();
        if (entries.Length > _maximumReceipts * 2 + 2 || entries.Any(Directory.Exists))
        {
            throw new FormatException("Operational-control receipt storage exceeds its finite artifact bounds.");
        }
        foreach (var entry in entries)
        {
            var fileName = Path.GetFileName(entry);
            if (fileName == ".custom-loop-mutations.lock")
            {
                continue;
            }
            if (IsInterruptedAtomicWrite(fileName))
            {
                _guard.GetFilePath(_root, fileName);
                File.Delete(entry);
                continue;
            }
            var identifier = fileName.EndsWith(".json", StringComparison.Ordinal) ? fileName[..^5]
                : fileName.EndsWith(".owner", StringComparison.Ordinal) ? fileName[..^6]
                : string.Empty;
            if (!CustomLoopArtifactIdentifier.IsValid(identifier, GovernedLoopOperationalPostureLimits.MaxOperationIdCharacters))
            {
                throw new FormatException("Operational-control receipt storage contains an unsupported artifact.");
            }
            _guard.GetFilePath(_root, fileName);
        }
    }

    private static bool IsInterruptedAtomicWrite(string fileName)
    {
        if (!fileName.StartsWith(".", StringComparison.Ordinal)
            || !fileName.EndsWith(".tmp", StringComparison.Ordinal))
        {
            return false;
        }
        var withoutPrefix = fileName[1..];
        var marker = withoutPrefix.LastIndexOf('.', withoutPrefix.Length - 5);
        if (marker <= 5)
        {
            return false;
        }
        var receiptName = withoutPrefix[..marker];
        var nonce = withoutPrefix[(marker + 1)..^4];
        return receiptName.EndsWith(".json", StringComparison.Ordinal)
            && CustomLoopArtifactIdentifier.IsValid(receiptName[..^5], GovernedLoopOperationalPostureLimits.MaxOperationIdCharacters)
            && nonce.Length == 32
            && nonce.All(character => character is >= '0' and <= '9' or >= 'a' and <= 'f');
    }

    private IReadOnlyList<string> ReceiptPaths()
        => Directory.EnumerateFiles(_root, "*.json", SearchOption.TopDirectoryOnly)
            .Select(path => _guard.GetFilePath(_root, Path.GetFileName(path)))
            .ToArray();

    private string ReceiptPath(string operationId) => _guard.GetFilePath(_root, operationId + ".json");

    private async Task<GovernedLoopOperationalControlReceipt> ReadAsync(string path, CancellationToken cancellationToken)
    {
        var bytes = await _guard.ReadAllBytesAsync(_root, path, _maximumReceiptBytes, "Operational-control receipt", cancellationToken).ConfigureAwait(false);
        var receipt = JsonSerializer.Deserialize<GovernedLoopOperationalControlReceipt>(bytes, _jsonOptions);
        if (!IsValid(receipt) || !bytes.AsSpan().SequenceEqual(Serialize(receipt!)))
        {
            throw new FormatException("Operational-control receipt is malformed or noncanonical.");
        }
        return Copy(receipt!);
    }

    private async Task WriteAsync(string path, GovernedLoopOperationalControlReceipt receipt, CancellationToken cancellationToken)
    {
        var bytes = Serialize(receipt);
        if (bytes.Length > _maximumReceiptBytes)
        {
            throw new FormatException("Operational-control receipt exceeds its configured byte bound.");
        }
        await _guard.WriteTextAtomicallyAsync(_root, path, Encoding.UTF8.GetString(bytes), cancellationToken).ConfigureAwait(false);
    }

    private static byte[] Serialize(GovernedLoopOperationalControlReceipt receipt)
        => JsonSerializer.SerializeToUtf8Bytes(receipt, _jsonOptions);

    private static bool IsValid(GovernedLoopOperationalControlReceipt? receipt)
    {
        if (receipt is null
            || receipt.SchemaVersion != GovernedLoopOperationalControlReceipt.CurrentSchemaVersion
            || !GovernedLoopOperationalContract.IsWorkspaceId(receipt.WorkspaceId)
            || !CustomLoopArtifactIdentifier.IsValid(receipt.OperationId, GovernedLoopOperationalPostureLimits.MaxOperationIdCharacters)
            || !CustomLoopArtifactIdentifier.IsValid(receipt.TargetId, GovernedLoopOperationalPostureLimits.MaxTargetIdCharacters)
            || !CustomLoopArtifactIdentifier.IsValid(receipt.ActorId, GovernedLoopOperationalPostureLimits.MaxActorIdCharacters)
            || !CustomLoopArtifactIdentifier.IsValid(receipt.SurfaceId, GovernedLoopOperationalPostureLimits.MaxSurfaceIdCharacters)
            || !Enum.IsDefined(receipt.Kind)
            || !Enum.IsDefined(receipt.State)
            || !Enum.IsDefined(receipt.Outcome)
            || receipt.ExpectedRevision <= 0
            || !GovernedLoopOperationalContract.IsHash(receipt.RequestHash)
            || !GovernedLoopOperationalContract.IsHash(receipt.ExpectedEvidenceHash)
            || !GovernedLoopOperationalContract.IsHash(receipt.AuthorityEvidenceHash)
            || receipt.PreviousContentHash is not null && !GovernedLoopOperationalContract.IsHash(receipt.PreviousContentHash)
            || !GovernedLoopOperationalContract.IsHash(receipt.ContentHash)
            || !GovernedLoopOperationalContract.IsUtc(receipt.RequestedAtUtc)
            || !GovernedLoopOperationalContract.IsUtc(receipt.UpdatedAtUtc)
            || receipt.UpdatedAtUtc < receipt.RequestedAtUtc
            || receipt.ReasonCode is not { Length: > 0 and <= 128 }
            || receipt.Progress is null
            || receipt.Progress.Count > GovernedLoopOperationalPostureLimits.MaxControlBatchItems
            || !IsStateShape(receipt)
            || receipt.State == GovernedLoopOperationalControlReceiptState.Pending && receipt.PreviousContentHash is not null
            || receipt.State != GovernedLoopOperationalControlReceiptState.Pending && receipt.PreviousContentHash is null
            || !IsProgressValid(receipt.Progress)
            || !string.Equals(GovernedLoopOperationalHash.Receipt(receipt), receipt.ContentHash, StringComparison.Ordinal))
        {
            return false;
        }
        return true;
    }

    private static bool IsProgressValid(IReadOnlyList<GovernedLoopOperationalControlProgress> progress)
    {
        string? prior = null;
        foreach (var item in progress)
        {
            if (item is null
                || !CustomLoopArtifactIdentifier.IsValid(item.TargetId, GovernedLoopOperationalPostureLimits.MaxTargetIdCharacters)
                || item.ExpectedRevision <= 0
                || !GovernedLoopOperationalContract.IsHash(item.ExpectedEvidenceHash)
                || !Enum.IsDefined(item.Status)
                || item.CurrentRevision is <= 0
                || item.CurrentEvidenceHash is not null && !GovernedLoopOperationalContract.IsHash(item.CurrentEvidenceHash)
                || item.ReasonCode is not { Length: > 0 and <= 128 }
                || prior is not null && string.Compare(prior, item.TargetId, StringComparison.Ordinal) >= 0)
            {
                return false;
            }
            prior = item.TargetId;
        }
        return true;
    }

    private static bool IsStateShape(GovernedLoopOperationalControlReceipt receipt)
        => receipt.State switch
        {
            GovernedLoopOperationalControlReceiptState.Pending => receipt.Progress.Count == 0 && receipt.Outcome == GovernedLoopOperationalControlStatus.OperationInProgress,
            GovernedLoopOperationalControlReceiptState.Mutating => receipt.Progress.Count > 0 && receipt.Outcome == GovernedLoopOperationalControlStatus.OperationInProgress,
            GovernedLoopOperationalControlReceiptState.Complete => receipt.Outcome is not GovernedLoopOperationalControlStatus.OperationInProgress and not GovernedLoopOperationalControlStatus.NeedsReview,
            GovernedLoopOperationalControlReceiptState.NeedsReview => receipt.Outcome == GovernedLoopOperationalControlStatus.NeedsReview,
            _ => false
        };

    private static bool IsSuccessor(GovernedLoopOperationalControlReceipt current, GovernedLoopOperationalControlReceipt replacement)
        => string.Equals(current.WorkspaceId, replacement.WorkspaceId, StringComparison.Ordinal)
            && string.Equals(current.OperationId, replacement.OperationId, StringComparison.Ordinal)
            && string.Equals(current.RequestHash, replacement.RequestHash, StringComparison.Ordinal)
            && current.Kind == replacement.Kind
            && string.Equals(current.TargetId, replacement.TargetId, StringComparison.Ordinal)
            && current.ExpectedRevision == replacement.ExpectedRevision
            && string.Equals(current.ExpectedEvidenceHash, replacement.ExpectedEvidenceHash, StringComparison.Ordinal)
            && string.Equals(current.ActorId, replacement.ActorId, StringComparison.Ordinal)
            && string.Equals(current.SurfaceId, replacement.SurfaceId, StringComparison.Ordinal)
            && string.Equals(current.AuthorityEvidenceHash, replacement.AuthorityEvidenceHash, StringComparison.Ordinal)
            && string.Equals(current.ContentHash, replacement.PreviousContentHash, StringComparison.Ordinal)
            && current.RequestedAtUtc == replacement.RequestedAtUtc
            && replacement.UpdatedAtUtc >= current.UpdatedAtUtc
            && (current.State == GovernedLoopOperationalControlReceiptState.Pending
                ? replacement.Progress.Count >= current.Progress.Count
                : replacement.Progress.Count == current.Progress.Count)
            && ProgressIsSuccessor(current.Progress, replacement.Progress)
            && (current.State == replacement.State
                || current.State == GovernedLoopOperationalControlReceiptState.Pending
                    && replacement.State is GovernedLoopOperationalControlReceiptState.Mutating or GovernedLoopOperationalControlReceiptState.Complete or GovernedLoopOperationalControlReceiptState.NeedsReview
                || current.State == GovernedLoopOperationalControlReceiptState.Mutating
                    && replacement.State is GovernedLoopOperationalControlReceiptState.Complete or GovernedLoopOperationalControlReceiptState.NeedsReview);

    private static bool ProgressIsSuccessor(
        IReadOnlyList<GovernedLoopOperationalControlProgress> current,
        IReadOnlyList<GovernedLoopOperationalControlProgress> replacement)
    {
        for (var index = 0; index < current.Count; index++)
        {
            var before = current[index];
            var after = replacement[index];
            if (!string.Equals(before.TargetId, after.TargetId, StringComparison.Ordinal)
                || before.ExpectedRevision != after.ExpectedRevision
                || !string.Equals(before.ExpectedEvidenceHash, after.ExpectedEvidenceHash, StringComparison.Ordinal))
            {
                return false;
            }
            if (Equals(before, after))
            {
                continue;
            }
            if (before.Status != GovernedLoopOperationalControlStatus.OperationInProgress
                || after.Status == GovernedLoopOperationalControlStatus.OperationInProgress)
            {
                return false;
            }
        }
        return true;
    }

    private static GovernedLoopOperationalControlReceipt Copy(GovernedLoopOperationalControlReceipt receipt)
        => new(
            receipt.SchemaVersion,
            receipt.WorkspaceId,
            receipt.OperationId,
            receipt.RequestHash,
            receipt.Kind,
            receipt.TargetId,
            receipt.ExpectedRevision,
            receipt.ExpectedEvidenceHash,
            receipt.ActorId,
            receipt.SurfaceId,
            receipt.AuthorityEvidenceHash,
            receipt.PreviousContentHash,
            receipt.RequestedAtUtc,
            receipt.UpdatedAtUtc,
            receipt.State,
            receipt.Outcome,
            receipt.ReasonCode,
            Array.AsReadOnly(receipt.Progress.Select(item => item with { }).ToArray()),
            receipt.ContentHash);

    private static bool TryCapture(
        GovernedLoopOperationalControlReceipt? source,
        out GovernedLoopOperationalControlReceipt captured)
    {
        captured = null!;
        if (source?.Progress is null)
        {
            return false;
        }

        try
        {
            var progress = source.Progress.ToArray();
            if (progress.Any(item => item is null))
            {
                return false;
            }
            captured = new GovernedLoopOperationalControlReceipt(
                source.SchemaVersion,
                source.WorkspaceId,
                source.OperationId,
                source.RequestHash,
                source.Kind,
                source.TargetId,
                source.ExpectedRevision,
                source.ExpectedEvidenceHash,
                source.ActorId,
                source.SurfaceId,
                source.AuthorityEvidenceHash,
                source.PreviousContentHash,
                source.RequestedAtUtc,
                source.UpdatedAtUtc,
                source.State,
                source.Outcome,
                source.ReasonCode,
                Array.AsReadOnly(progress.Select(item => item with { }).ToArray()),
                source.ContentHash);
            return IsValid(captured);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            captured = null!;
            return false;
        }
    }

    private static GovernedLoopOperationalControlReceiptStoreResult Result(
        GovernedLoopOperationalControlReceiptStoreStatus status,
        GovernedLoopOperationalControlReceipt? receipt = null)
        => new(status, receipt is null ? null : Copy(receipt));

    private static bool IsCorrupt(Exception exception)
        => exception is FormatException or JsonException or InvalidDataException or OverflowException or ArgumentException;

    private static bool IsUnavailable(Exception exception)
        => exception is IOException or UnauthorizedAccessException or InvalidOperationException or TimeoutException or NotSupportedException;
}
