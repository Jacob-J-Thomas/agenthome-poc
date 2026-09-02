using EmbodySense.Core.Application.Loops.Execution.Reconciliation.Models;

namespace EmbodySense.Core.Application.Tests.Loops.Execution.Reconciliation;

public sealed class GovernedLoopEffectReconciliationRequestBoundsTests
{
    [Theory]
    [InlineData(1, 1)]
    [InlineData(100, 1024)]
    public void Case_list_request_accepts_finite_page_and_cursor_bounds(int maximumCount, int cursorLength)
    {
        var request = new GovernedLoopEffectReconciliationCaseListRequest(maximumCount, new string('c', cursorLength));

        Assert.Equal(maximumCount, request.MaximumCount);
        Assert.Equal(cursorLength, request.Cursor!.Length);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(101)]
    public void Case_list_request_rejects_page_size_outside_finite_bounds(int maximumCount)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new GovernedLoopEffectReconciliationCaseListRequest(maximumCount));
    }

    [Fact]
    public void Case_list_request_rejects_oversized_cursor()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new GovernedLoopEffectReconciliationCaseListRequest(1, new string('c', 1025)));
    }

    [Theory]
    [InlineData(1, 1)]
    [InlineData(100, 1024)]
    public void Probe_registry_list_request_accepts_finite_page_and_cursor_bounds(int maximumCount, int cursorLength)
    {
        var request = new GovernedLoopEffectReconciliationProbeRegistryListRequest(maximumCount, new string('c', cursorLength));

        Assert.Equal(maximumCount, request.MaximumCount);
        Assert.Equal(cursorLength, request.Cursor!.Length);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(101)]
    public void Probe_registry_list_request_rejects_page_size_outside_finite_bounds(int maximumCount)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new GovernedLoopEffectReconciliationProbeRegistryListRequest(maximumCount));
    }

    [Fact]
    public void Probe_registry_list_request_rejects_oversized_cursor()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new GovernedLoopEffectReconciliationProbeRegistryListRequest(1, new string('c', 1025)));
    }
}
