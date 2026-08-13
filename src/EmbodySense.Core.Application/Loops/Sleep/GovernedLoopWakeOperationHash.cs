using System.Security.Cryptography;
using System.Text;

namespace EmbodySense.Core.Application.Loops.Sleep;

internal static class GovernedLoopWakeOperationHash
{
    private const string Domain = "governed-loop-wake-continuation-operation-v1";

    internal static string Create(string wakeId)
        => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Domain + "\n" + wakeId))).ToLowerInvariant();
}
