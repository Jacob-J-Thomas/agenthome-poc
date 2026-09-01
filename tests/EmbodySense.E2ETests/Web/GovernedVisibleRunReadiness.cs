using EmbodySense.Core.Common.Loops.Custom;

namespace EmbodySense.E2ETests.Web;

internal static class GovernedVisibleRunReadiness
{
    private const string ExactRunMarker = " · exact run ";
    private const string ExactRunSuffix = " is open in Runs.";

    public static void RequireUnambiguousBaseline(IReadOnlyCollection<string> baselineRunIds)
    {
        ArgumentNullException.ThrowIfNull(baselineRunIds);
        if (baselineRunIds.Any(runId => !CustomLoopArtifactIdentifier.IsValid(runId))
            || baselineRunIds.Distinct(StringComparer.Ordinal).Count() != baselineRunIds.Count)
        {
            throw new InvalidOperationException("The visible run projection was malformed or duplicated before invocation.");
        }
    }

    public static string RequireNewSelectedRunId(
        string invocationStatus,
        IReadOnlyCollection<string> baselineRunIds,
        IReadOnlyCollection<string> visibleRunIds,
        IReadOnlyCollection<string> selectedRunIds)
    {
        ArgumentNullException.ThrowIfNull(invocationStatus);
        ArgumentNullException.ThrowIfNull(baselineRunIds);
        ArgumentNullException.ThrowIfNull(visibleRunIds);
        ArgumentNullException.ThrowIfNull(selectedRunIds);

        var markerIndex = invocationStatus.IndexOf(ExactRunMarker, StringComparison.Ordinal);
        if (markerIndex < 0 || markerIndex != invocationStatus.LastIndexOf(ExactRunMarker, StringComparison.Ordinal))
        {
            throw new InvalidOperationException($"The visible invocation did not report one exact admitted run: {invocationStatus}");
        }

        var runIdStart = markerIndex + ExactRunMarker.Length;
        var suffixIndex = invocationStatus.IndexOf(ExactRunSuffix, runIdStart, StringComparison.Ordinal);
        var runId = suffixIndex < 0 || suffixIndex + ExactRunSuffix.Length != invocationStatus.Length
            ? ""
            : invocationStatus[runIdStart..suffixIndex];
        if (!CustomLoopArtifactIdentifier.IsValid(runId))
        {
            throw new InvalidOperationException($"The visible invocation reported a malformed exact run identity: {invocationStatus}");
        }

        if (baselineRunIds.Contains(runId, StringComparer.Ordinal))
        {
            throw new InvalidOperationException($"The visible invocation selected stale run `{runId}` instead of a newly admitted run.");
        }

        if (visibleRunIds.Any(candidate => !CustomLoopArtifactIdentifier.IsValid(candidate))
            || visibleRunIds.Distinct(StringComparer.Ordinal).Count() != visibleRunIds.Count
            || visibleRunIds.Count(candidate => string.Equals(candidate, runId, StringComparison.Ordinal)) != 1)
        {
            throw new InvalidOperationException($"The exact admitted run `{runId}` was missing or duplicated in the visible run projection.");
        }

        if (selectedRunIds.Count != 1
            || !CustomLoopArtifactIdentifier.IsValid(selectedRunIds.Single())
            || !string.Equals(selectedRunIds.Single(), runId, StringComparison.Ordinal))
        {
            throw new InvalidOperationException($"The exact admitted run `{runId}` was not the one uniquely selected in the visible run projection.");
        }

        return runId;
    }
}
