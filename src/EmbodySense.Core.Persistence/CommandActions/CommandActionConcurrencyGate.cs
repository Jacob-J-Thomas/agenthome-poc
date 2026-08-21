using System.Diagnostics;
using EmbodySense.Core.Application.CommandActions;
using EmbodySense.Core.Common.CommandActions;
using EmbodySense.Core.Common.Workspace;
using EmbodySense.Core.Persistence.Loops;

namespace EmbodySense.Core.Persistence.CommandActions;

/// <summary>Enforces registered command concurrency through workspace-scoped cross-process slot leases.</summary>
public sealed class CommandActionConcurrencyGate : ICommandActionConcurrencyGate
{
    private static readonly TimeSpan _retryDelay = TimeSpan.FromMilliseconds(25);
    private readonly CustomLoopArtifactPathGuard _guard;
    private readonly string _root;

    /// <summary>Creates a workspace-scoped command concurrency gate.</summary>
    public CommandActionConcurrencyGate(WorkspacePaths paths)
    {
        ArgumentNullException.ThrowIfNull(paths);
        _root = Path.Combine(paths.AgentPath, "loops", "execution", "command-actions", "concurrency");
        _guard = new CustomLoopArtifactPathGuard(paths.RootPath);
    }

    /// <inheritdoc />
    public bool IsAvailable => true;

    /// <inheritdoc />
    public async Task<IAsyncDisposable?> TryAcquireAsync(
        string templateHash,
        int maximumConcurrency,
        TimeSpan waitLimit,
        CancellationToken cancellationToken = default)
    {
        if (!CommandActionFingerprint.IsCanonicalSha256(templateHash)
            || maximumConcurrency is < 1 or > CommandActionContractLimits.MaxConcurrency
            || waitLimit <= TimeSpan.Zero
            || waitLimit > TimeSpan.FromSeconds(30))
        {
            return null;
        }
        var templateRoot = Path.Combine(_root, templateHash);
        _guard.PrepareRoot(templateRoot);
        var deadline = Stopwatch.GetTimestamp() + (long)(waitLimit.TotalSeconds * Stopwatch.Frequency);
        do
        {
            cancellationToken.ThrowIfCancellationRequested();
            for (var index = 0; index < maximumConcurrency; index++)
            {
                var path = _guard.GetFilePath(templateRoot, $"slot-{index:D4}.lock");
                try
                {
                    var stream = new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None, 1, FileOptions.WriteThrough);
                    return new CommandActionConcurrencyLease(stream);
                }
                catch (IOException)
                {
                    // Another process owns this exact slot.
                }
            }
            if (Stopwatch.GetTimestamp() >= deadline)
            {
                return null;
            }
            await Task.Delay(_retryDelay, cancellationToken).ConfigureAwait(false);
        }
        while (true);
    }
}
