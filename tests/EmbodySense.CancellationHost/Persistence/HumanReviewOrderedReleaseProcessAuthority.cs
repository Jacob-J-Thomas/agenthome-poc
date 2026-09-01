using EmbodySense.Core.Application.HumanReview;
using EmbodySense.Core.Application.HumanReview.Models;

namespace EmbodySense.CancellationHost.Persistence;

internal sealed class HumanReviewOrderedReleaseProcessAuthority : IHumanReviewContinuationAuthoritySource
{
    private readonly string? _readyPath;
    private readonly string? _releasePath;
    private int _barrierEntered;

    internal HumanReviewOrderedReleaseProcessAuthority(string? readyPath = null, string? releasePath = null)
    {
        if ((readyPath is null) != (releasePath is null)) throw new ArgumentException("The race barrier requires both ready and release paths.");
        _readyPath = readyPath;
        _releasePath = releasePath;
    }

    public async Task<HumanReviewContinuationAuthorityReadResult> ReadAsync(HumanReviewContinuationAuthorityQuery query, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (_readyPath is not null && Interlocked.Exchange(ref _barrierEntered, 1) == 0)
        {
            await File.WriteAllTextAsync(_readyPath, "ready", cancellationToken);
            await WaitForFileAsync(_releasePath!, TimeSpan.FromSeconds(30), cancellationToken);
        }

        return new HumanReviewContinuationAuthorityReadResult(HumanReviewContinuationAuthorityReadStatus.Current);
    }

    private static async Task WaitForFileAsync(string path, TimeSpan timeout, CancellationToken cancellationToken)
    {
        using var timeoutCancellation = new CancellationTokenSource(timeout);
        using var linkedCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutCancellation.Token);
        while (!File.Exists(path)) await Task.Delay(TimeSpan.FromMilliseconds(10), linkedCancellation.Token);
    }
}
