using System.Diagnostics;

namespace EmbodySense.Core.Persistence.Tests.Verification;

internal sealed record CrossProcessReadinessChild(
    string Label,
    CrossProcessProcess Process,
    string ReadyPath,
    string ResultPath)
{
    internal CrossProcessProcessOwnership Ownership => Process.Ownership;
}
