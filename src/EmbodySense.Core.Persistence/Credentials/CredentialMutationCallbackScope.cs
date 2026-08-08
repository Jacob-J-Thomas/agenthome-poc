namespace EmbodySense.Core.Persistence.Credentials;

internal sealed class CredentialMutationCallbackScope : IDisposable
{
    private static readonly object _activeTargetsGate = new();
    private static readonly Dictionary<string, int> _activeTargetCounts = new(StringComparer.Ordinal);
    private static readonly AsyncLocal<CredentialMutationCallbackScope?> _current = new();
    private readonly CredentialMutationCallbackScope? _prior;
    private readonly string _target;
    private bool _disposed;

    private CredentialMutationCallbackScope(string target, CredentialMutationCallbackScope? prior)
    {
        _target = target;
        _prior = prior;
    }

    internal static CredentialMutationCallbackScope Enter(string target)
    {
        var scope = new CredentialMutationCallbackScope(target, _current.Value);
        lock (_activeTargetsGate)
        {
            _activeTargetCounts[target] = _activeTargetCounts.GetValueOrDefault(target) + 1;
        }

        _current.Value = scope;
        return scope;
    }

    internal static bool IsActive(string target)
    {
        for (var scope = _current.Value; scope is not null; scope = scope._prior)
        {
            if (string.Equals(scope._target, target, StringComparison.Ordinal))
            {
                return true;
            }
        }

        lock (_activeTargetsGate)
        {
            return _activeTargetCounts.ContainsKey(target);
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        if (ReferenceEquals(_current.Value, this))
        {
            _current.Value = _prior;
        }

        lock (_activeTargetsGate)
        {
            var remaining = _activeTargetCounts[_target] - 1;
            if (remaining == 0)
            {
                _activeTargetCounts.Remove(_target);
            }
            else
            {
                _activeTargetCounts[_target] = remaining;
            }
        }
    }
}
