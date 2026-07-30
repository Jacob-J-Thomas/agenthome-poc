using EmbodySense.Core.Startup.Loops.Execution.Models;
using System.Security.Cryptography;
using System.Text;
using EmbodySense.Core.Startup.Loops.Execution;

namespace EmbodySense.Web.Services;

/// <summary>
/// Creates strong monitor validators from the exact public run summary and canonical artifact hash.
/// </summary>
public static class LoopRunMonitorEtag
{
    /// <summary>
    /// Creates a quoted SHA-256 entity tag that changes when monitor-visible run identity or state changes.
    /// </summary>
    /// <param name="summary">The public run summary.</param>
    /// <param name="artifactHash">The canonical durable artifact hash associated with that summary.</param>
    /// <returns>A quoted lowercase hexadecimal entity tag.</returns>
    public static string Create(LoopRunSummarySnapshot summary, string artifactHash)
    {
        ArgumentNullException.ThrowIfNull(summary);
        ArgumentException.ThrowIfNullOrWhiteSpace(artifactHash);

        var identity = string.Join('\n',
            artifactHash,
            summary.Id,
            summary.LoopId,
            summary.AdmissionOperationId,
            summary.DefinitionVersion.ToString(System.Globalization.CultureInfo.InvariantCulture),
            summary.LifecycleVersion.ToString(System.Globalization.CultureInfo.InvariantCulture),
            summary.Status,
            summary.CreatedAtUtc.UtcTicks.ToString(System.Globalization.CultureInfo.InvariantCulture),
            summary.UpdatedAtUtc.UtcTicks.ToString(System.Globalization.CultureInfo.InvariantCulture),
            summary.CompletedAtUtc?.UtcTicks.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? string.Empty,
            summary.Iteration.ToString(System.Globalization.CultureInfo.InvariantCulture),
            summary.NextStepIndex.ToString(System.Globalization.CultureInfo.InvariantCulture),
            summary.FailureCode ?? string.Empty,
            summary.IsDeleted ? "1" : "0");
        return $"\"{Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(identity))).ToLowerInvariant()}\"";
    }
}
