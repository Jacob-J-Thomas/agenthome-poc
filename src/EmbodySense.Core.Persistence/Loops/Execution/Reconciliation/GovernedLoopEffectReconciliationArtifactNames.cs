using System.Globalization;
using EmbodySense.Core.Persistence.Loops.Execution.Reconciliation.Models;

namespace EmbodySense.Core.Persistence.Loops.Execution.Reconciliation;

internal static class GovernedLoopEffectReconciliationArtifactNames
{
    internal static string CaseVersionFileName(string storageKey, long version, string contentHash)
        => $"{GovernedLoopEffectReconciliationPersistenceLimits.CaseFilePrefix}{storageKey}.{version.ToString(CultureInfo.InvariantCulture)}.{contentHash}.json";

    internal static string CaseHeadFileName(string storageKey)
        => $"{GovernedLoopEffectReconciliationPersistenceLimits.CaseFilePrefix}{storageKey}.head";

    internal static string ReceiptFileName(string operationKey)
        => $"{GovernedLoopEffectReconciliationPersistenceLimits.ReceiptFilePrefix}{operationKey}.json";

    internal static string JournalFileName(string operationKey)
        => $"{GovernedLoopEffectReconciliationPersistenceLimits.JournalFilePrefix}{operationKey}.json";

    internal static bool TryParseCaseVersionFile(string fileName, out string storageKey, out long version, out string contentHash)
    {
        storageKey = string.Empty;
        version = 0;
        contentHash = string.Empty;
        if (!fileName.StartsWith(GovernedLoopEffectReconciliationPersistenceLimits.CaseFilePrefix, StringComparison.Ordinal)
            || !fileName.EndsWith(".json", StringComparison.Ordinal))
        {
            return false;
        }

        var parts = fileName[GovernedLoopEffectReconciliationPersistenceLimits.CaseFilePrefix.Length..^5].Split('.');
        if (parts.Length != 3
            || !IsHash(parts[0])
            || !long.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out version)
            || version is < 1 or > GovernedLoopEffectReconciliationPersistenceLimits.MaximumCaseVersions
            || !IsHash(parts[2]))
        {
            storageKey = string.Empty;
            version = 0;
            contentHash = string.Empty;
            return false;
        }

        storageKey = parts[0];
        contentHash = parts[2];
        return true;
    }

    internal static bool TryParseCaseHeadFile(string fileName, out string storageKey)
    {
        storageKey = string.Empty;
        if (!fileName.StartsWith(GovernedLoopEffectReconciliationPersistenceLimits.CaseFilePrefix, StringComparison.Ordinal)
            || !fileName.EndsWith(".head", StringComparison.Ordinal))
        {
            return false;
        }

        var candidate = fileName[GovernedLoopEffectReconciliationPersistenceLimits.CaseFilePrefix.Length..^5];
        if (!IsHash(candidate))
        {
            return false;
        }

        storageKey = candidate;
        return true;
    }

    internal static bool TryParseReceiptFile(string fileName, out string operationKey)
        => TryParseSingleHashFile(fileName, GovernedLoopEffectReconciliationPersistenceLimits.ReceiptFilePrefix, out operationKey);

    internal static bool TryParseJournalFile(string fileName, out string operationKey)
        => TryParseSingleHashFile(fileName, GovernedLoopEffectReconciliationPersistenceLimits.JournalFilePrefix, out operationKey);

    internal static bool IsReconciliationArtifact(string fileName)
        => TryParseCaseVersionFile(fileName, out _, out _, out _)
            || TryParseCaseHeadFile(fileName, out _)
            || TryParseReceiptFile(fileName, out _)
            || TryParseJournalFile(fileName, out _);

    internal static bool IsInterruptedAtomicWrite(string fileName)
    {
        if (!fileName.StartsWith(".", StringComparison.Ordinal) || !fileName.EndsWith(".tmp", StringComparison.Ordinal))
        {
            return false;
        }

        var withoutMarkers = fileName[1..^4];
        var nonceSeparator = withoutMarkers.LastIndexOf('.');
        if (nonceSeparator <= 0)
        {
            return false;
        }

        var destination = withoutMarkers[..nonceSeparator];
        var nonce = withoutMarkers[(nonceSeparator + 1)..];
        return (TryParseCaseVersionFile(destination, out _, out _, out _)
                || TryParseCaseHeadFile(destination, out _)
                || TryParseReceiptFile(destination, out _)
                || TryParseJournalFile(destination, out _))
            && nonce.Length == 32
            && nonce.All(character => character is >= '0' and <= '9' or >= 'a' and <= 'f');
    }

    internal static string StorageKey(string caseId)
        => GovernedLoopEffectReconciliationPersistenceHash.Compute("case", caseId);

    internal static string OperationKey(string operationId)
        => GovernedLoopEffectReconciliationPersistenceHash.Compute("operation", operationId);

    private static bool TryParseSingleHashFile(string fileName, string prefix, out string key)
    {
        key = string.Empty;
        if (!fileName.StartsWith(prefix, StringComparison.Ordinal) || !fileName.EndsWith(".json", StringComparison.Ordinal))
        {
            return false;
        }

        var candidate = fileName[prefix.Length..^5];
        if (!IsHash(candidate))
        {
            return false;
        }

        key = candidate;
        return true;
    }

    private static bool IsHash(string value)
        => value.Length == 64 && value.All(character => character is >= '0' and <= '9' or >= 'a' and <= 'f');
}
