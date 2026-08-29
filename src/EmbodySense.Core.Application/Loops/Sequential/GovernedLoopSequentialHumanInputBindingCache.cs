using EmbodySense.Core.Application.Loops.Sequential.Models;
using EmbodySense.Core.Common.Loops.HumanInput.Checkpoints;

namespace EmbodySense.Core.Application.Loops.Sequential;

/// <summary>Caches only in-process exact bindings already rehydrated before a selected node is claimed or emits start evidence.</summary>
internal sealed class GovernedLoopSequentialHumanInputBindingCache
{
    private readonly IGovernedLoopSequentialHumanInputBindingSource? _source;
    private readonly Dictionary<string, GovernedLoopSequentialHumanInputBindingReadResult> _results = new(StringComparer.Ordinal);

    internal GovernedLoopSequentialHumanInputBindingCache(IGovernedLoopSequentialHumanInputBindingSource? source)
    {
        _source = source;
    }

    internal async Task<GovernedLoopSequentialHumanInputBindingReadResult> ResolveAsync(
        GovernedLoopHumanInputWaitingCheckpoint checkpoint,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(checkpoint);
        var cacheKey = checkpoint.Binding.CheckpointId + "\n" + checkpoint.CheckpointHash;
        if (_results.TryGetValue(cacheKey, out var retained))
        {
            return retained;
        }

        if (_source is null)
        {
            return new GovernedLoopSequentialHumanInputBindingReadResult(GovernedLoopSequentialHumanInputBindingReadStatus.Unavailable, null);
        }

        GovernedLoopSequentialHumanInputBindingReadResult resolved;
        try
        {
            resolved = await _source.ResolveAsync(checkpoint, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            resolved = new GovernedLoopSequentialHumanInputBindingReadResult(GovernedLoopSequentialHumanInputBindingReadStatus.Unavailable, null);
        }

        if (resolved is null
            || !Enum.IsDefined(resolved.Status)
            || resolved.Status == GovernedLoopSequentialHumanInputBindingReadStatus.Ready && resolved.Binding is null
            || resolved.Status != GovernedLoopSequentialHumanInputBindingReadStatus.Ready && resolved.Binding is not null)
        {
            resolved = new GovernedLoopSequentialHumanInputBindingReadResult(GovernedLoopSequentialHumanInputBindingReadStatus.Invalid, null);
        }

        _results.TryAdd(cacheKey, resolved);
        return resolved;
    }
}
