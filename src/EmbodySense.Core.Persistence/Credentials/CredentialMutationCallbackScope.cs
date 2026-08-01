namespace EmbodySense.Core.Persistence.Credentials;

internal sealed class CredentialMutationCallbackScope : IDisposable
{
    private static readonly ThreadLocal<HashSet<string>?> _activeTargets = new();
    private readonly string _target;
    private bool _disposed;

    private CredentialMutationCallbackScope(string target)
    {
        _target = target;
    }

    internal static CredentialMutationCallbackScope Enter(string target)
    {
        var activeTargets = _activeTargets.Value ??= new HashSet<string>(StringComparer.Ordinal);
        activeTargets.Add(target);
        return new CredentialMutationCallbackScope(target);
    }

    internal static bool IsActive(string target)
    {
        return _activeTargets.Value?.Contains(target) == true;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        var activeTargets = _activeTargets.Value;
        if (activeTargets is null)
        {
            return;
        }

        activeTargets.Remove(_target);
        if (activeTargets.Count == 0)
        {
            _activeTargets.Value = null;
        }
    }
}
