using EmbodySense.Core.Application.Loops.EffectAttempts;
using EmbodySense.Core.Application.Loops.EffectAttempts.Models;

namespace EmbodySense.CancellationHost.Persistence;

internal sealed class HumanReviewOrderedReleaseProcessDiagnosticReadStore : IGovernedLoopEffectAttemptReadStore
{
    private const int MaximumDiagnosticCharacters = 512;
    private readonly Func<string, string, long, CancellationToken, Task<GovernedLoopEffectAttemptReadResult>> _read;
    private readonly Action<string> _report;

    internal HumanReviewOrderedReleaseProcessDiagnosticReadStore(IGovernedLoopEffectAttemptReadStore inner, Action<string>? report = null)
        : this(inner is null ? throw new ArgumentNullException(nameof(inner)) : inner.ReadAsync, report)
    {
    }

    internal HumanReviewOrderedReleaseProcessDiagnosticReadStore(Func<string, string, long, CancellationToken, Task<GovernedLoopEffectAttemptReadResult>> read, Action<string>? report = null)
    {
        _read = read ?? throw new ArgumentNullException(nameof(read));
        _report = report ?? WriteToStandardError;
    }

    public async Task<GovernedLoopEffectAttemptReadResult> ReadAsync(string workspaceId, string operationId, long effectGeneration, CancellationToken cancellationToken = default)
    {
        GovernedLoopEffectAttemptReadResult result;
        try
        {
            result = await _read(workspaceId, operationId, effectGeneration, cancellationToken);
        }
        catch (Exception exception)
        {
            Report($"Human Review ordered-release race canonical attempt read threw: exception-type={exception.GetType().FullName ?? exception.GetType().Name}.");
            throw;
        }

        if (result is not { Status: GovernedLoopEffectAttemptReadStatus.Current })
        {
            Report($"Human Review ordered-release race canonical attempt read was non-Current: status={result?.Status.ToString() ?? "<null>"}.");
        }

        return result;
    }

    private void Report(string diagnostic)
    {
        try
        {
            _report(BoundDiagnostic(diagnostic));
        }
        catch
        {
        }
    }

    private static void WriteToStandardError(string diagnostic)
    {
        Console.Error.WriteLine(diagnostic);
        Console.Error.Flush();
    }

    private static string BoundDiagnostic(string diagnostic)
        => diagnostic.Length <= MaximumDiagnosticCharacters
            ? diagnostic
            : diagnostic[..MaximumDiagnosticCharacters];
}
