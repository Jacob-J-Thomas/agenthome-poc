using System.Diagnostics;

namespace EmbodySense.Core.Persistence.Tests.Verification;

internal sealed record CrossProcessReadinessChild(
    string Label,
    Process Process,
    string ReadyPath,
    string ResultPath,
    CrossProcessProcessOwnership Ownership);
