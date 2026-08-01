using System.Text;
using EmbodySense.Core.Application.Loops.ReceiptRetention;
using EmbodySense.Core.Application.Loops.ReceiptRetention.Models;
using EmbodySense.Core.Common.Loops.Custom;
using EmbodySense.Core.Common.Loops.Custom.Retention;
using EmbodySense.Core.Common.Loops.Models.Custom.Retention;

namespace EmbodySense.Core.Persistence.Loops;

/// <summary>
/// Retains a bounded immutable terminal cleanup journal per operation identity so later journal rotation cannot authorize reuse.
/// </summary>
internal sealed class CustomLoopReceiptCleanupHistoryStore(CustomLoopArtifactPathGuard pathGuard, string root, CustomLoopReceiptArtifactClass artifactClass)
{
    private readonly CustomLoopArtifactPathGuard _pathGuard = pathGuard ?? throw new ArgumentNullException(nameof(pathGuard));
    private readonly string _root = Path.GetFullPath(root ?? throw new ArgumentNullException(nameof(root)));
    private readonly CustomLoopReceiptArtifactClass _artifactClass = artifactClass;

    internal async Task<CustomLoopReceiptCleanupJournal?> ReadAsync(string operationId, CancellationToken cancellationToken)
    {
        var safeOperationId = CustomLoopArtifactIdentifier.Require(operationId, nameof(operationId), CustomLoopLimits.MaxMutationOperationIdCharacters);
        var inventory = await ReadInventoryAsync(cancellationToken);
        return inventory.Journals.GetValueOrDefault(safeOperationId);
    }

    internal async Task<(int Count, long Utf8Bytes)> InspectAsync(CancellationToken cancellationToken)
    {
        var inventory = await ReadInventoryAsync(cancellationToken);
        return (inventory.Journals.Count, inventory.Utf8Bytes);
    }

    internal async Task<CustomLoopReceiptQuotaExhaustionReason> ArchiveAsync(CustomLoopReceiptCleanupJournal journal, CancellationToken cancellationToken)
    {
        ValidateTerminal(journal);
        var inventory = await ReadInventoryAsync(cancellationToken);
        if (inventory.Journals.TryGetValue(journal.Request.OperationId, out var existing))
        {
            return CustomLoopReceiptRetentionContractCodec.CleanupJournalsEqual(existing, journal)
                ? CustomLoopReceiptQuotaExhaustionReason.None
                : throw new FormatException($"Cleanup history operation `{journal.Request.OperationId}` conflicts with its immutable terminal journal.");
        }

        var bytes = CustomLoopReceiptRetentionContractCodec.SerializeCleanupJournal(journal);
        if (inventory.Journals.Count >= CustomLoopReceiptRetentionPolicy.MaxCleanupHistoryEntryCount)
        {
            return CustomLoopReceiptQuotaExhaustionReason.CleanupHistoryCountLimit;
        }

        if (bytes.LongLength > CustomLoopReceiptRetentionPolicy.MaxCleanupHistoryUtf8Bytes - inventory.Utf8Bytes)
        {
            return CustomLoopReceiptQuotaExhaustionReason.CleanupHistoryByteLimit;
        }

        _pathGuard.PrepareRoot(_root);
        var path = _pathGuard.GetFilePath(_root, journal.Request.OperationId + ".json");
        await _pathGuard.WriteTextAtomicallyAsync(_root, path, Encoding.UTF8.GetString(bytes), cancellationToken);
        return CustomLoopReceiptQuotaExhaustionReason.None;
    }

    private async Task<(IReadOnlyDictionary<string, CustomLoopReceiptCleanupJournal> Journals, long Utf8Bytes)> ReadInventoryAsync(CancellationToken cancellationToken)
    {
        if (!_pathGuard.DirectoryExists(_root))
        {
            return (new Dictionary<string, CustomLoopReceiptCleanupJournal>(StringComparer.Ordinal), 0);
        }

        if (Directory.EnumerateDirectories(_root, "*", SearchOption.TopDirectoryOnly).Take(1).Any())
        {
            throw new FormatException("Cleanup history cannot contain subdirectories.");
        }

        var paths = Directory.EnumerateFiles(_root, "*", SearchOption.TopDirectoryOnly).Take(CustomLoopReceiptRetentionPolicy.MaxCleanupHistoryEntryCount + 2).ToArray();
        if (paths.Length > CustomLoopReceiptRetentionPolicy.MaxCleanupHistoryEntryCount + 1)
        {
            throw new FormatException("Cleanup history exceeds its bounded inventory ceiling.");
        }

        var journals = new Dictionary<string, CustomLoopReceiptCleanupJournal>(StringComparer.Ordinal);
        long totalBytes = 0;
        foreach (var path in paths.OrderBy(item => item, StringComparer.Ordinal))
        {
            var fileName = Path.GetFileName(path);
            if (IsAtomicWriteTemp(fileName))
            {
                _pathGuard.DeleteFile(_root, path);
                continue;
            }

            if (!string.Equals(Path.GetExtension(fileName), ".json", StringComparison.Ordinal))
            {
                throw new FormatException($"Cleanup history contains an unrecognized artifact `{fileName}`.");
            }

            var operationId = Path.GetFileNameWithoutExtension(fileName);
            if (!CustomLoopArtifactIdentifier.IsValid(operationId, CustomLoopLimits.MaxMutationOperationIdCharacters))
            {
                throw new FormatException($"Cleanup history contains an invalid operation artifact `{fileName}`.");
            }

            var bytes = await _pathGuard.ReadAllBytesAsync(_root, path, CustomLoopReceiptRetentionPolicy.MaxCleanupJournalUtf8Bytes, "Cleanup history journal", cancellationToken);
            totalBytes = checked(totalBytes + bytes.LongLength);
            if (totalBytes > CustomLoopReceiptRetentionPolicy.MaxCleanupHistoryUtf8Bytes)
            {
                throw new FormatException("Cleanup history exceeds its aggregate UTF-8 byte ceiling.");
            }

            var journal = CustomLoopReceiptRetentionContractCodec.DeserializeCleanupJournal(bytes);
            ValidateTerminal(journal);
            if (!string.Equals(operationId, journal.Request.OperationId, StringComparison.Ordinal) || !journals.TryAdd(operationId, journal))
            {
                throw new FormatException($"Cleanup history journal `{fileName}` conflicts with its operation identity.");
            }
        }

        if (journals.Count > CustomLoopReceiptRetentionPolicy.MaxCleanupHistoryEntryCount)
        {
            throw new FormatException("Cleanup history exceeds its completed-operation identity ceiling.");
        }

        return (journals, totalBytes);
    }

    private void ValidateTerminal(CustomLoopReceiptCleanupJournal journal)
    {
        CustomLoopReceiptRetentionContractValidator.ValidateCleanupJournal(journal);
        if (journal.Request.ArtifactClass != _artifactClass || journal.Stage is not (CustomLoopReceiptCleanupStage.Completed or CustomLoopReceiptCleanupStage.CommittedWithAuditWarning or CustomLoopReceiptCleanupStage.AbandonedConflict or CustomLoopReceiptCleanupStage.Degraded))
        {
            throw new FormatException("Cleanup history can retain only terminal journals for its exact receipt artifact class.");
        }
    }

    private static bool IsAtomicWriteTemp(string fileName)
    {
        const string Suffix = ".tmp";
        const int GuidLength = 32;
        if (fileName.Length == 0 || fileName[0] != '.' || !fileName.EndsWith(Suffix, StringComparison.Ordinal))
        {
            return false;
        }

        var guidStart = fileName.Length - Suffix.Length - GuidLength;
        if (guidStart <= 2 || fileName[guidStart - 1] != '.')
        {
            return false;
        }

        var target = fileName[1..(guidStart - 1)];
        return string.Equals(Path.GetExtension(target), ".json", StringComparison.Ordinal)
            && CustomLoopArtifactIdentifier.IsValid(Path.GetFileNameWithoutExtension(target), CustomLoopLimits.MaxMutationOperationIdCharacters)
            && Guid.TryParseExact(fileName.Substring(guidStart, GuidLength), "N", out _);
    }
}
