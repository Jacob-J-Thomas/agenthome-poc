using EmbodySense.Core.Common.Loops.Custom;
using SurfaceModels = EmbodySense.Core.Startup.Loops.Execution.Reconciliation.Models;

namespace EmbodySense.Core.Startup.Loops.Execution.Reconciliation;

internal static class GovernedLoopEffectReconciliationSurfaceGuard
{
    private const string WorkspacePrefix = "workspace-sha256:";
    internal const int MaxPageSize = 100;
    internal const int MaxCursorCharacters = 1024;
    internal const int MaxHistoryEntries = 64;
    internal const int MaxDetailCharacters = 1024;
    internal const int Sha256Characters = 64;

    internal static string Identifier(string? value, string parameterName)
        => CustomLoopArtifactIdentifier.Require(value, parameterName, 120);

    internal static string? OptionalIdentifier(string? value, string parameterName)
        => value is null ? null : Identifier(value, parameterName);

    internal static string WorkspaceId(string? value, string parameterName)
    {
        if (value?.Length != WorkspacePrefix.Length + Sha256Characters
            || !value.StartsWith(WorkspacePrefix, StringComparison.Ordinal)
            || value[WorkspacePrefix.Length..].Any(character => character is not (>= '0' and <= '9') and not (>= 'a' and <= 'f')))
        {
            throw new ArgumentException("An exact canonical workspace SHA-256 scope is required.", parameterName);
        }

        return value;
    }

    internal static string Hash(string? value, string parameterName)
    {
        if (value?.Length != Sha256Characters || value.Any(character => character is not (>= '0' and <= '9') and not (>= 'a' and <= 'f')))
        {
            throw new ArgumentException("A canonical lowercase SHA-256 hash is required.", parameterName);
        }

        return value;
    }

    internal static string? OptionalHash(string? value, string parameterName)
        => value is null ? null : Hash(value, parameterName);

    internal static string? Detail(string? value, string parameterName)
    {
        if (value is null)
        {
            return null;
        }
        if (value.Length > MaxDetailCharacters || value.Any(char.IsControl))
        {
            throw new ArgumentException("Operator detail must be bounded normalized text.", parameterName);
        }

        return value;
    }

    internal static DateTimeOffset Utc(DateTimeOffset value, string parameterName)
        => value != default && value.Offset == TimeSpan.Zero
            ? value
            : throw new ArgumentException("A non-default UTC timestamp is required.", parameterName);

    internal static DateTimeOffset? OptionalUtc(DateTimeOffset? value, string parameterName)
        => value is null ? null : Utc(value.Value, parameterName);

    internal static int PageSize(int value, string parameterName)
        => value is >= 1 and <= MaxPageSize
            ? value
            : throw new ArgumentOutOfRangeException(parameterName, "The reconciliation page size must be from 1 through 100.");

    internal static string? Cursor(string? value, string parameterName)
    {
        if (value is null)
        {
            return null;
        }
        if (value.Length is 0 or > MaxCursorCharacters || value.Any(char.IsControl))
        {
            throw new ArgumentException("The opaque reconciliation cursor is malformed or too large.", parameterName);
        }

        return value;
    }

    internal static IReadOnlyList<T> Items<T>(IEnumerable<T>? values, int maximumCount, string parameterName)
    {
        if (values is null)
        {
            throw new ArgumentNullException(parameterName);
        }
        var captured = values.Take(maximumCount + 1).ToArray();
        if (captured.Length > maximumCount || captured.Any(value => value is null))
        {
            throw new ArgumentOutOfRangeException(parameterName, "The bounded reconciliation projection is malformed or too large.");
        }

        return Array.AsReadOnly(captured);
    }

    internal static string? AuthorizationEvidence(
        SurfaceModels.GovernedLoopEffectReconciliationAuthorizationStatus status,
        string? actorId,
        string? scopeId,
        string? evidenceHash,
        string parameterName)
    {
        var captured = OptionalHash(evidenceHash, parameterName);
        if (status == SurfaceModels.GovernedLoopEffectReconciliationAuthorizationStatus.Ready && (actorId is null || scopeId is null || captured is null)
            || status != SurfaceModels.GovernedLoopEffectReconciliationAuthorizationStatus.Ready && (actorId is not null || scopeId is not null || captured is not null))
        {
            throw new ArgumentException("Ready reconciliation authorization requires complete actor, scope, and evidence terms; closed results omit every authority term.", parameterName);
        }

        return captured;
    }

    internal static IReadOnlyList<SurfaceModels.GovernedLoopEffectReconciliationCaseSummary> PageItems(
        SurfaceModels.GovernedLoopEffectReconciliationPageStatus status,
        IEnumerable<SurfaceModels.GovernedLoopEffectReconciliationCaseSummary>? values,
        string? nextCursor,
        string parameterName)
    {
        var captured = Items(values, MaxPageSize, parameterName);
        if (status != SurfaceModels.GovernedLoopEffectReconciliationPageStatus.Ready && (captured.Count != 0 || nextCursor is not null))
        {
            throw new ArgumentException("Only a ready reconciliation page may carry items or a continuation.", parameterName);
        }

        return captured;
    }

    internal static IReadOnlyList<SurfaceModels.GovernedLoopEffectReconciliationContractProjection> CatalogItems(
        SurfaceModels.GovernedLoopEffectReconciliationProbeCatalogStatus status,
        IEnumerable<SurfaceModels.GovernedLoopEffectReconciliationContractProjection>? values,
        string? nextCursor,
        string parameterName)
    {
        var captured = Items(values, MaxPageSize, parameterName);
        if (status != SurfaceModels.GovernedLoopEffectReconciliationProbeCatalogStatus.Ready && (captured.Count != 0 || nextCursor is not null))
        {
            throw new ArgumentException("Only a ready probe catalog may carry contracts or a continuation.", parameterName);
        }

        return captured;
    }

    internal static SurfaceModels.GovernedLoopEffectReconciliationCaseDetail? OperationDetail(
        SurfaceModels.GovernedLoopEffectReconciliationOperationStatus status,
        SurfaceModels.GovernedLoopEffectReconciliationCaseDetail? detail,
        string parameterName)
    {
        if (detail is not null && status is not (SurfaceModels.GovernedLoopEffectReconciliationOperationStatus.Applied
            or SurfaceModels.GovernedLoopEffectReconciliationOperationStatus.Replayed
            or SurfaceModels.GovernedLoopEffectReconciliationOperationStatus.Found
            or SurfaceModels.GovernedLoopEffectReconciliationOperationStatus.Conflict))
        {
            throw new ArgumentException("Only a safely observed successful, replayed, found, or conflicting operation may carry case detail.", parameterName);
        }

        return detail;
    }

    internal static SurfaceModels.GovernedLoopEffectReconciliationCaseDetail? ReadDetail(
        SurfaceModels.GovernedLoopEffectReconciliationReadStatus status,
        SurfaceModels.GovernedLoopEffectReconciliationCaseDetail? detail,
        string parameterName)
    {
        if ((status == SurfaceModels.GovernedLoopEffectReconciliationReadStatus.Found) != (detail is not null))
        {
            throw new ArgumentException("Only a found reconciliation read may carry case detail.", parameterName);
        }

        return detail;
    }

    internal static SurfaceModels.GovernedLoopEffectReconciliationResolutionProjection? Resolution(
        SurfaceModels.GovernedLoopEffectReconciliationResolutionReadStatus status,
        SurfaceModels.GovernedLoopEffectReconciliationResolutionProjection? resolution,
        string parameterName)
    {
        if ((status == SurfaceModels.GovernedLoopEffectReconciliationResolutionReadStatus.Found) != (resolution is not null))
        {
            throw new ArgumentException("Only a found resolution read may carry an immutable resolution.", parameterName);
        }

        return resolution;
    }
}
