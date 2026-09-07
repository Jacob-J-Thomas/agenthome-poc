using EmbodySense.Core.Application.Loops.EffectAttempts;
using EmbodySense.Core.Application.Loops.EffectAttempts.Models;

namespace EmbodySense.CancellationHost.Persistence;

internal sealed class HumanReviewOrderedReleaseProcessDiagnosticReadStore(IGovernedLoopEffectAttemptReadStore inner) : IGovernedLoopEffectAttemptReadStore
{
    public async Task<GovernedLoopEffectAttemptReadResult> ReadAsync(string workspaceId, string operationId, long effectGeneration, CancellationToken cancellationToken = default)
    {
        try
        {
            var result = await inner.ReadAsync(workspaceId, operationId, effectGeneration, cancellationToken);
            if (result is not { Status: GovernedLoopEffectAttemptReadStatus.Current })
            {
                Console.Error.WriteLine($"Human Review ordered-release race canonical attempt read was non-Current: operation={operationId}, generation={effectGeneration}, status={result?.Status.ToString() ?? "<null>"}.");
                Console.Error.Flush();
            }

            return result;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine($"Human Review ordered-release race canonical attempt read threw: operation={operationId}, generation={effectGeneration}, exception={exception.GetType().FullName}: {exception.Message}");
            Console.Error.Flush();
            throw;
        }
    }
}
