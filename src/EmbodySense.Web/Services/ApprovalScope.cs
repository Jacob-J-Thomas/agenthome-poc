namespace EmbodySense.Web.Services;

internal sealed class ApprovalScope(AsyncLocal<string?> currentOwnerConnectionId, string? previousOwnerConnectionId) : IDisposable
{
    private bool _disposed;

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        currentOwnerConnectionId.Value = previousOwnerConnectionId;
        _disposed = true;
    }
}
