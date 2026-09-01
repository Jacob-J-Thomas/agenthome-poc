using EmbodySense.Core.Common.Workspace;

namespace EmbodySense.E2ETests.Web;

internal sealed class HumanReviewAuthorityParkingLease : IAsyncDisposable
{
    private const string UnreadableDocument = "{";
    private readonly Entry[] _entries;
    private int _restored;

    private HumanReviewAuthorityParkingLease(Entry[] entries)
        => _entries = entries;

    internal static async Task<HumanReviewAuthorityParkingLease> ParkAsync(WorkspacePaths paths, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(paths);
        var candidatePaths = new[] { paths.AuthorityProfilesDocumentPath, paths.AuthorityProfilesProofPath };
        var entries = new Entry[candidatePaths.Length];
        for (var index = 0; index < candidatePaths.Length; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var path = candidatePaths[index];
            var exists = File.Exists(path);
            entries[index] = new Entry(path, exists, exists ? await File.ReadAllBytesAsync(path, cancellationToken).ConfigureAwait(false) : null);
        }

        var lease = new HumanReviewAuthorityParkingLease(entries);
        try
        {
            foreach (var entry in entries.Where(item => item.Exists))
            {
                await lease.ReplaceAsync(entry.Path, System.Text.Encoding.UTF8.GetBytes(UnreadableDocument), cancellationToken).ConfigureAwait(false);
            }

            return lease;
        }
        catch
        {
            await lease.RestoreAsync().ConfigureAwait(false);
            throw;
        }
    }

    public async ValueTask DisposeAsync()
        => await RestoreAsync().ConfigureAwait(false);

    internal async Task RestoreAsync(CancellationToken cancellationToken = default)
    {
        if (Interlocked.Exchange(ref _restored, 1) != 0)
        {
            return;
        }

        foreach (var entry in _entries)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (entry.Exists)
            {
                await ReplaceAsync(entry.Path, entry.Content!, cancellationToken).ConfigureAwait(false);
            }
            else if (File.Exists(entry.Path))
            {
                File.Delete(entry.Path);
            }
        }
    }

    private async Task ReplaceAsync(string path, byte[] content, CancellationToken cancellationToken)
    {
        var directory = Path.GetDirectoryName(path) ?? throw new InvalidOperationException("The authority artifact directory was unavailable.");
        Directory.CreateDirectory(directory);
        var temporaryPath = Path.Combine(directory, ".human-review-response-loss-" + Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            await File.WriteAllBytesAsync(temporaryPath, content, cancellationToken).ConfigureAwait(false);
            for (var attempt = 0; attempt < 100; attempt++)
            {
                try
                {
                    File.Move(temporaryPath, path, true);
                    return;
                }
                catch (IOException) when (attempt < 99)
                {
                    await Task.Delay(50, cancellationToken).ConfigureAwait(false);
                }
                catch (UnauthorizedAccessException) when (attempt < 99)
                {
                    await Task.Delay(50, cancellationToken).ConfigureAwait(false);
                }
            }
        }
        finally
        {
            if (File.Exists(temporaryPath))
            {
                File.Delete(temporaryPath);
            }
        }
    }

    private sealed record Entry(string Path, bool Exists, byte[]? Content);
}
