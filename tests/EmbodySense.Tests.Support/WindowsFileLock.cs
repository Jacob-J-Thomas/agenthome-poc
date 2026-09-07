using System.Diagnostics;
using System.Globalization;

namespace EmbodySense.Tests.Support;

public sealed class WindowsFileLock : IDisposable
{
    private const int MaximumProcessEvidenceCharacters = 4_096;
    private static readonly TimeSpan _readyTimeout = TimeSpan.FromSeconds(30);

    private readonly Process _process;
    private readonly Task<string> _processError;
    private readonly Task<string> _processOutput;
    private readonly string _lockPath;
    private readonly string _readyPath;
    private readonly string _releasePath;
    private readonly string _scriptPath;
    private int _disposed;

    public WindowsFileLock(string path, string? coordinationDirectory = null)
        : this(path, coordinationDirectory, "lock")
    {
    }

    public static WindowsFileLock OpenRestrictiveReader(string path, string? coordinationDirectory = null) => new(path, coordinationDirectory, "read");

    private WindowsFileLock(string path, string? coordinationDirectory, string mode)
    {
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException("Windows file locks are required by this test fixture.");
        }

        var lockDirectory = Path.GetDirectoryName(path) ?? throw new ArgumentException("The lock path must have a parent directory.", nameof(path));
        var directory = coordinationDirectory ?? lockDirectory;
        Directory.CreateDirectory(lockDirectory);
        Directory.CreateDirectory(directory);
        var suffix = Guid.NewGuid().ToString("N");
        _readyPath = Path.Combine(directory, $".{suffix}.ready");
        _releasePath = Path.Combine(directory, $".{suffix}.release");
        _scriptPath = Path.Combine(directory, $".{suffix}.ps1");
        _lockPath = path;
        File.WriteAllText(_scriptPath, """
            param([string]$lockPath, [string]$readyPath, [string]$releasePath, [string]$mode)
            $access = if ($mode -eq 'read') { [System.IO.FileAccess]::Read } else { [System.IO.FileAccess]::ReadWrite }
            $fileMode = if ($mode -eq 'read') { [System.IO.FileMode]::Open } else { [System.IO.FileMode]::OpenOrCreate }
            $stream = [System.IO.FileStream]::new($lockPath, $fileMode, $access, [System.IO.FileShare]::Read)
            try {
                if ($mode -eq 'lock') { $stream.Lock(0, 1) }
                [System.IO.File]::WriteAllText($readyPath, 'ready')
                while (-not [System.IO.File]::Exists($releasePath)) { Start-Sleep -Milliseconds 10 }
            }
            finally {
                $stream.Dispose()
            }
            """);

        var start = new ProcessStartInfo
        {
            FileName = "powershell.exe",
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardError = true,
            RedirectStandardOutput = true,
        };
        start.ArgumentList.Add("-NoProfile");
        start.ArgumentList.Add("-ExecutionPolicy");
        start.ArgumentList.Add("Bypass");
        start.ArgumentList.Add("-File");
        start.ArgumentList.Add(_scriptPath);
        start.ArgumentList.Add(path);
        start.ArgumentList.Add(_readyPath);
        start.ArgumentList.Add(_releasePath);
        start.ArgumentList.Add(mode);
        _process = Process.Start(start) ?? throw new IOException("The test fixture could not start the external workspace-host process.");
        _processOutput = _process.StandardOutput.ReadToEndAsync();
        _processError = _process.StandardError.ReadToEndAsync();
        WaitForReady();
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        _ = ReleaseStopAndDescribeProcess(throwOnCleanupFailure: true);
    }

    private void WaitForReady()
    {
        var timeout = Stopwatch.StartNew();
        while (!File.Exists(_readyPath) && !_process.HasExited && timeout.Elapsed < _readyTimeout)
        {
            Thread.Sleep(10);
        }

        if (File.Exists(_readyPath))
        {
            return;
        }

        var evidence = ReleaseStopAndDescribeProcess(throwOnCleanupFailure: false);
        throw new IOException($"The test fixture could not acquire the external workspace-host lock within {_readyTimeout.TotalSeconds:0} seconds. {evidence}");
    }

    private string ReleaseStopAndDescribeProcess(bool throwOnCleanupFailure)
    {
        var termination = "released";
        var cleanupFailures = new List<string>();
        try
        {
            File.WriteAllText(_releasePath, "release");
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            termination = "release-failed";
            cleanupFailures.Add(DescribeCleanupFailure(exception));
        }

        try
        {
            if (!_process.WaitForExit(5000))
            {
                termination = "killed";
                _process.Kill(entireProcessTree: true);
                if (!_process.WaitForExit(5000))
                {
                    cleanupFailures.Add("Process did not exit within five seconds after termination.");
                }
            }
        }
        catch (Exception exception) when (exception is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            cleanupFailures.Add(DescribeCleanupFailure(exception));
        }

        try
        {
            if (!Task.WaitAll([_processOutput, _processError], 5000))
            {
                cleanupFailures.Add("Redirected process streams did not close within five seconds.");
            }
        }
        catch (AggregateException exception)
        {
            cleanupFailures.Add(DescribeCleanupFailure(exception.GetBaseException()));
        }

        var hasExited = false;
        var exitCode = "<unavailable>";
        try
        {
            hasExited = _process.HasExited;
            if (hasExited)
            {
                exitCode = _process.ExitCode.ToString(CultureInfo.InvariantCulture);
            }
        }
        catch (InvalidOperationException exception)
        {
            cleanupFailures.Add(DescribeCleanupFailure(exception));
        }

        var evidence = $"pid={_process.Id} state={(hasExited ? "exited" : "running")} exit={exitCode} ready={File.Exists(_readyPath)} ready_path={_readyPath} lock={_lockPath} termination={termination} stdout={DescribeProcessEvidence(_processOutput)} stderr={DescribeProcessEvidence(_processError)}";
        _process.Dispose();
        TryDelete(_readyPath, cleanupFailures);
        TryDelete(_releasePath, cleanupFailures);
        TryDelete(_scriptPath, cleanupFailures);
        if (cleanupFailures.Count == 0)
        {
            return evidence;
        }

        var cleanup = string.Join(" | ", cleanupFailures.Select(BoundProcessEvidence));
        if (throwOnCleanupFailure)
        {
            throw new IOException($"The external workspace-host process could not be cleaned up. {evidence} cleanup={cleanup}");
        }

        return $"{evidence} cleanup={cleanup}";
    }

    private static string DescribeProcessEvidence(Task<string> evidence)
    {
        if (!evidence.IsCompleted)
        {
            return "<pending>";
        }

        if (evidence.IsCanceled)
        {
            return "<cancelled>";
        }

        if (evidence.IsFaulted)
        {
            return $"<faulted:{BoundProcessEvidence(evidence.Exception?.GetBaseException().Message ?? "unknown")}>";
        }

        return BoundProcessEvidence(evidence.Result);
    }

    private static string BoundProcessEvidence(string evidence)
        => evidence.Length <= MaximumProcessEvidenceCharacters
            ? evidence
            : "<truncated>" + evidence[^MaximumProcessEvidenceCharacters..];

    private static string DescribeCleanupFailure(Exception exception)
        => $"{exception.GetType().Name}:{BoundProcessEvidence(exception.Message)}";

    private static void TryDelete(string path, ICollection<string> cleanupFailures)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            cleanupFailures.Add(DescribeCleanupFailure(exception));
        }
    }
}
