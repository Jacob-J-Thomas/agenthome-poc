namespace EmbodySense.Core.Startup.HumanReview.Models;

/// <summary>Returns one bounded detached page of Human Review summaries.</summary>
/// <param name="Status">The closed page outcome.</param>
/// <param name="Items">The detached summaries; private binding and authority details are omitted.</param>
/// <param name="ContinuationCursor">The opaque cursor returned by canonical run discovery.</param>
public sealed record HumanReviewPage(HumanReviewPageStatus Status, IReadOnlyList<HumanReviewSummary> Items, string? ContinuationCursor)
{
    /// <summary>Gets an immutable defensive copy of the page items.</summary>
    public IReadOnlyList<HumanReviewSummary> Items { get; } = Items is null ? null! : Array.AsReadOnly(Items.ToArray());
}
