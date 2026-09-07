namespace EmbodySense.Core.Persistence.Tests.Verification;

/// <summary>
/// Owns the bounded CancellationHost restrictive-reader child and its marker protocol.
/// </summary>
internal sealed class WindowsRestrictiveReaderProcess : IDisposable
{
    private const string Operation = "windows-restrictive-reader";
    private static readonly TimeSpan _readinessTimeout = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan _completionTimeout = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan _terminationTimeout = TimeSpan.FromSeconds(5);

    private readonly CrossProcessProcess _process;
    private readonly string _readyPath;
    private readonly string _releasePath;
    private readonly string _resultPath;
    private int _released;
    private int _disposed;

    private WindowsRestrictiveReaderProcess(CrossProcessProcess process, string readyPath, string releasePath, string resultPath)
    {
        _process = process;
        _readyPath = readyPath;
        _releasePath = releasePath;
        _resultPath = resultPath;
    }

    internal static async Task<WindowsRestrictiveReaderProcess> StartAsync(string path, string coordinationDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentException.ThrowIfNullOrWhiteSpace(coordinationDirectory);
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException("The restrictive-reader process requires Windows.");
        }

        var suffix = Guid.NewGuid().ToString("N");
        var readyPath = Path.Combine(coordinationDirectory, $".{suffix}.restrictive-reader.ready");
        var releasePath = Path.Combine(coordinationDirectory, $".{suffix}.restrictive-reader.release");
        var resultPath = Path.Combine(coordinationDirectory, $".{suffix}.restrictive-reader.result");
        var process = CancellationHostProcess.StartAppHostOwned(Operation, path, readyPath, releasePath, resultPath);
        var reader = new WindowsRestrictiveReaderProcess(process, readyPath, releasePath, resultPath);
        try
        {
            await CrossProcessReadinessDiagnostics.WaitForChildrenReadyAsync(
                Operation,
                [reader.CreateReadinessChild()],
                _readinessTimeout);
            return reader;
        }
        catch
        {
            reader.Dispose();
            throw;
        }
    }

    internal async Task ReleaseAsync()
    {
        ThrowIfDisposed();
        if (Interlocked.Exchange(ref _released, 1) != 0)
        {
            return;
        }

        await File.WriteAllTextAsync(_releasePath, "release");
        await CrossProcessReadinessDiagnostics.WaitForChildrenCompletedAsync(
            Operation,
            "release",
            [CreateReadinessChild()],
            _completionTimeout,
            _completionTimeout);
        var result = await File.ReadAllTextAsync(_resultPath);
        if (!string.Equals(result, "released", StringComparison.Ordinal))
        {
            throw new InvalidOperationException($"The restrictive-reader child published an unexpected result `{result}`.");
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        try
        {
            if (Interlocked.Exchange(ref _released, 1) == 0)
            {
                try
                {
                    File.WriteAllText(_releasePath, "release");
                }
                catch (IOException)
                {
                }
            }

            if (!_process.HasExited)
            {
                try
                {
                    _process.Ownership.TerminateProcessTree();
                }
                catch (InvalidOperationException) when (_process.HasExited)
                {
                }

                try
                {
                    _process.WaitForExitAsync().WaitAsync(_terminationTimeout).GetAwaiter().GetResult();
                }
                catch (TimeoutException)
                {
                }
                catch (InvalidOperationException) when (_process.HasExited)
                {
                }
            }
        }
        finally
        {
            _process.Dispose();
            DeleteMarker(_readyPath);
            DeleteMarker(_releasePath);
            DeleteMarker(_resultPath);
        }
    }

    private CrossProcessReadinessChild CreateReadinessChild()
        => new(Operation, _process, _readyPath, _resultPath);

    private void ThrowIfDisposed()
    {
        if (Volatile.Read(ref _disposed) != 0)
        {
            throw new ObjectDisposedException(nameof(WindowsRestrictiveReaderProcess));
        }
    }

    private static void DeleteMarker(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
