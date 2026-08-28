using System.Diagnostics;

namespace EmbodySense.Core.Persistence.Tests.Verification;

internal sealed record CrossProcessReadinessChild(
    string Label,
    CrossProcessProcess Process,
    string ReadyPath,
    string ResultPath,
    Task<string>? StandardOutputTask = null,
    Task<string>? StandardErrorTask = null,
    CancellationTokenSource? EvidenceCancellation = null)
{
    internal CrossProcessProcessOwnership Ownership => Process.Ownership;
}
