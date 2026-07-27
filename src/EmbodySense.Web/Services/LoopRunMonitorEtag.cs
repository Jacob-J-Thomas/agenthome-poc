using System.Security.Cryptography;
using System.Text;
using EmbodySense.Core.Startup.Loops.Execution;

namespace EmbodySense.Web.Services;

public static class LoopRunMonitorEtag
{
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
