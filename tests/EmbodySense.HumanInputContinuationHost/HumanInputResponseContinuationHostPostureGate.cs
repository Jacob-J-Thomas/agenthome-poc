namespace EmbodySense.HumanInputContinuationHost;

internal sealed class HumanInputResponseContinuationHostPostureGate
{
    private static readonly TimeSpan _releaseTimeout = TimeSpan.FromSeconds(30);

    private readonly string _readyPath;
    private readonly string _releasePath;

    private HumanInputResponseContinuationHostPostureGate(int readOrdinal, string readyPath, string releasePath)
    {
        ReadOrdinal = readOrdinal;
        _readyPath = readyPath;
        _releasePath = releasePath;
    }

    internal int ReadOrdinal { get; }

    internal static HumanInputResponseContinuationHostPostureGate? Create(int readOrdinal, string? readyPath, string? releasePath)
        => readOrdinal > 0
            && !string.IsNullOrWhiteSpace(readyPath)
            && !string.IsNullOrWhiteSpace(releasePath)
            && readyPath != "-"
            && releasePath != "-"
                ? new HumanInputResponseContinuationHostPostureGate(readOrdinal, readyPath, releasePath)
                : null;

    internal void WaitIfMatched(int readOrdinal)
    {
        if (readOrdinal != ReadOrdinal)
        {
            return;
        }

        File.WriteAllText(_readyPath, "continuation-posture-ready");
        if (File.Exists(_releasePath))
        {
            return;
        }

        var directory = Path.GetDirectoryName(_releasePath);
        if (string.IsNullOrWhiteSpace(directory))
        {
            throw new DirectoryNotFoundException("The Human Input continuation posture release marker has no directory.");
        }

        using var watcher = new FileSystemWatcher(directory, Path.GetFileName(_releasePath));
        using var released = new ManualResetEventSlim(File.Exists(_releasePath));
        FileSystemEventHandler onCreated = (_, _) => released.Set();
        RenamedEventHandler onRenamed = (_, _) => released.Set();
        watcher.Created += onCreated;
        watcher.Changed += onCreated;
        watcher.Renamed += onRenamed;
        watcher.EnableRaisingEvents = true;
        if (File.Exists(_releasePath))
        {
            released.Set();
        }

        if (!released.Wait(_releaseTimeout))
        {
            throw new TimeoutException($"The Human Input continuation posture release marker `{_releasePath}` was not published.");
        }
    }
}
