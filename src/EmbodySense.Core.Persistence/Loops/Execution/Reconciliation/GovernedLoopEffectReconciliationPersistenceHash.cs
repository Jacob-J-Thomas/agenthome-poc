using System.Security.Cryptography;
using System.Text;

namespace EmbodySense.Core.Persistence.Loops.Execution.Reconciliation;

internal static class GovernedLoopEffectReconciliationPersistenceHash
{
    internal static string Compute(string domain, string value)
        => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes($"embodysense.governed-loop-effect-reconciliation-persistence.v1\n{domain}\n{value}"))).ToLowerInvariant();
}
