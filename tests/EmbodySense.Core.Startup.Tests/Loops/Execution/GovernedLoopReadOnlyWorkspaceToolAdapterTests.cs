using EmbodySense.Core.Common.Loops.Custom.Execution;
using EmbodySense.Core.Common.Loops.Models.Custom;
using EmbodySense.Core.Startup.Loops.Execution;

namespace EmbodySense.Core.Startup.Tests.Loops.Execution;

public sealed class GovernedLoopReadOnlyWorkspaceToolAdapterTests
{
    private static readonly DateTimeOffset _now = new(2026, 8, 11, 9, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task Projects_only_the_immutable_admitted_read_only_catalog_without_loading_ambient_authority()
    {
        var adapter = new GovernedLoopReadOnlyWorkspaceToolAdapter(new FixedTimeProvider(_now));

        var result = await adapter.ResolveAsync(
            "assistant-role",
            [CustomLoopToolAssignment.Search, CustomLoopToolAssignment.Read]);

        Assert.True(result.IsValid);
        Assert.Equal([CustomLoopToolAssignment.Search, CustomLoopToolAssignment.Read], result.AdmittedMaximum);
        Assert.Equal(
            [CustomLoopToolAssignment.List, CustomLoopToolAssignment.Read, CustomLoopToolAssignment.Search],
            result.CurrentRoleCeiling);
        Assert.Equal([CustomLoopToolAssignment.Read, CustomLoopToolAssignment.Search], result.EffectiveAssignments);
        Assert.Equal(_now, result.EvaluatedAtUtc);
        Assert.Contains("non-granting", result.Detail, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(CustomLoopToolAssignment.Unknown, CustomLoopToolAssignment.Read)]
    [InlineData(CustomLoopToolAssignment.Read, CustomLoopToolAssignment.Read)]
    public async Task Malformed_or_unsupported_assignments_fail_closed(
        CustomLoopToolAssignment first,
        CustomLoopToolAssignment second)
    {
        var adapter = new GovernedLoopReadOnlyWorkspaceToolAdapter(new FixedTimeProvider(_now));

        var result = await adapter.ResolveAsync("assistant-role", [first, second]);

        Assert.False(result.IsValid);
        Assert.Empty(result.EffectiveAssignments);
        Assert.Contains("malformed", result.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Cancellation_and_invalid_arguments_stop_before_projection()
    {
        var adapter = new GovernedLoopReadOnlyWorkspaceToolAdapter(new FixedTimeProvider(_now));
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => adapter.ResolveAsync("assistant-role", [], cancellation.Token));
        await Assert.ThrowsAsync<ArgumentException>(() => adapter.ResolveAsync(" ", []));
        await Assert.ThrowsAsync<ArgumentNullException>(() => adapter.ResolveAsync("assistant-role", null!));
    }

    private sealed class FixedTimeProvider(DateTimeOffset value) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => value;
    }
}
