using System.Diagnostics;

namespace EmbodySense.Tests.Support;

public sealed class WindowsFileLock : IDisposable
{
    private static readonly TimeSpan _readyTimeout = TimeSpan.FromSeconds(30);

    private readonly Process _process;
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
            CreateNoWindow = true
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
        WaitForReady();
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        try
        {
            File.WriteAllText(_releasePath, "release");
            if (!_process.WaitForExit(5000))
            {
                _process.Kill(entireProcessTree: true);
                _process.WaitForExit();
            }
        }
        finally
        {
            _process.Dispose();
            File.Delete(_readyPath);
            File.Delete(_releasePath);
            File.Delete(_scriptPath);
        }
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

        Dispose();
        throw new IOException("The test fixture could not acquire the external workspace-host lock.");
    }
}
