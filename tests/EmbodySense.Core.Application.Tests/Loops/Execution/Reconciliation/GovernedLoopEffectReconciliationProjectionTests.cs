using EmbodySense.Core.Application.Loops.Execution.Reconciliation.Models;

namespace EmbodySense.Core.Application.Tests.Loops.Execution.Reconciliation;

public sealed class GovernedLoopEffectReconciliationProjectionTests
{
    private const string CaseHash = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    private const string BindingHash = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";

    [Fact]
    public void Exact_case_reference_retains_only_validated_identity_and_hash_coordinates()
    {
        var reference = new GovernedLoopEffectReconciliationCaseReference("case-1", 2, CaseHash, BindingHash);

        Assert.Equal("case-1", reference.CaseId);
        Assert.Equal(2, reference.CaseVersion);
        Assert.Equal(CaseHash, reference.ContentHash);
        Assert.Equal(BindingHash, reference.BindingHash);
    }

    [Fact]
    public void Exact_case_reference_rejects_invalid_coordinates()
    {
        Assert.Throws<ArgumentException>(() => new GovernedLoopEffectReconciliationCaseReference("INVALID", 1, CaseHash, BindingHash));
        Assert.Throws<ArgumentOutOfRangeException>(() => new GovernedLoopEffectReconciliationCaseReference("case-1", 0, CaseHash, BindingHash));
        Assert.Throws<ArgumentException>(() => new GovernedLoopEffectReconciliationCaseReference("case-1", 1, CaseHash.ToUpperInvariant(), BindingHash));
        Assert.Throws<ArgumentException>(() => new GovernedLoopEffectReconciliationCaseReference("case-1", 1, CaseHash, BindingHash[..^1]));
    }

    [Theory]
    [InlineData(GovernedLoopEffectReconciliationCaseSummaryStatus.Open)]
    [InlineData(GovernedLoopEffectReconciliationCaseSummaryStatus.Assessed)]
    [InlineData(GovernedLoopEffectReconciliationCaseSummaryStatus.Accepted)]
    [InlineData(GovernedLoopEffectReconciliationCaseSummaryStatus.Quarantined)]
    [InlineData(GovernedLoopEffectReconciliationCaseSummaryStatus.Resolved)]
    public void Case_summary_accepts_each_explicit_non_unknown_posture(GovernedLoopEffectReconciliationCaseSummaryStatus status)
    {
        var summary = CreateSummary(status);

        Assert.Equal(status, summary.Status);
    }

    [Fact]
    public void Case_summary_rejects_unknown_and_undefined_postures()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => CreateSummary(GovernedLoopEffectReconciliationCaseSummaryStatus.Unknown));
        Assert.Throws<ArgumentOutOfRangeException>(() => CreateSummary((GovernedLoopEffectReconciliationCaseSummaryStatus)99));
    }

    [Fact]
    public void Case_page_captures_detached_read_only_summaries_and_bounded_cursor()
    {
        var first = CreateSummary(GovernedLoopEffectReconciliationCaseSummaryStatus.Open);
        var second = new GovernedLoopEffectReconciliationCaseSummary("case-2", 2, BindingHash, CaseHash, GovernedLoopEffectReconciliationCaseSummaryStatus.Assessed);
        var source = new[] { first };

        var page = new GovernedLoopEffectReconciliationCaseListPage(GovernedLoopEffectReconciliationCaseListStatus.Ready, source, new string('c', 1024));
        source[0] = second;

        var retained = Assert.Single(page.Cases);
        Assert.Equal(GovernedLoopEffectReconciliationCaseListStatus.Ready, page.Status);
        Assert.Equal(first, retained);
        Assert.NotSame(first, retained);
        Assert.Throws<NotSupportedException>(() => ((IList<GovernedLoopEffectReconciliationCaseSummary>)page.Cases)[0] = second);
        Assert.Equal(1024, page.NextCursor!.Length);
    }

    [Fact]
    public void Case_page_enforces_exact_finite_entry_and_cursor_bounds()
    {
        var summary = CreateSummary(GovernedLoopEffectReconciliationCaseSummaryStatus.Open);
        var maximum = Enumerable.Repeat(summary, 100).ToArray();

        var page = new GovernedLoopEffectReconciliationCaseListPage(GovernedLoopEffectReconciliationCaseListStatus.Ready, maximum, "c");

        Assert.Equal(100, page.Cases.Count);
        Assert.Throws<ArgumentOutOfRangeException>(() => new GovernedLoopEffectReconciliationCaseListPage(GovernedLoopEffectReconciliationCaseListStatus.Ready, Enumerable.Repeat(summary, 101).ToArray(), null));
        Assert.Throws<ArgumentException>(() => new GovernedLoopEffectReconciliationCaseListPage(GovernedLoopEffectReconciliationCaseListStatus.Ready, [null!], null));
        Assert.Throws<ArgumentOutOfRangeException>(() => new GovernedLoopEffectReconciliationCaseListPage(GovernedLoopEffectReconciliationCaseListStatus.Ready, [], new string('c', 1025)));
    }

    private static GovernedLoopEffectReconciliationCaseSummary CreateSummary(GovernedLoopEffectReconciliationCaseSummaryStatus status)
        => new("case-1", 1, CaseHash, BindingHash, status);
}
