namespace EmbodySense.Core.Persistence.WorkspaceActions;

/// <summary>Owns one cross-process exact-target serialization lease.</summary>
internal sealed class WorkspaceActionTargetLease(FileStream stream, IDisposable? namespaceOwnership = null) : IDisposable
{
    private FileStream? _stream = stream ?? throw new ArgumentNullException(nameof(stream));
    private IDisposable? _namespaceOwnership = namespaceOwnership;

    /// <inheritdoc />
    public void Dispose()
    {
        try
        {
            Interlocked.Exchange(ref _stream, null)?.Dispose();
        }
        finally
        {
            Interlocked.Exchange(ref _namespaceOwnership, null)?.Dispose();
        }
    }
}
