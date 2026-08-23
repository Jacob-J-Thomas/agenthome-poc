using EmbodySense.Core.Common.LocalWorkspace.Actions;

namespace EmbodySense.Core.Common.Tests.LocalWorkspace.Actions;

public sealed class WorkspaceRelativeFileTargetTests
{
    [Theory]
    [InlineData("notes/today.md", "notes/today.md", 2)]
    [InlineData("a", "a", 1)]
    [InlineData("caf\u00e9/report.txt", "caf\u00e9/report.txt", 2)]
    public void Exact_portable_targets_are_retained(string value, string expected, int depth)
    {
        Assert.True(WorkspaceRelativeFileTarget.TryParse(value, out var target, out var reason), reason);
        Assert.Equal(expected, target!.Value);
        Assert.Equal(depth, target.Depth);
        Assert.Equal(expected.Split('/'), target.Segments);
    }

    [Theory]
    [InlineData("")]
    [InlineData("/absolute")]
    [InlineData("C:/device")]
    [InlineData("../escape")]
    [InlineData("a/../escape")]
    [InlineData("a//b")]
    [InlineData("a/")]
    [InlineData("a\\b")]
    [InlineData("*.txt")]
    [InlineData("name?")]
    [InlineData("con")]
    [InlineData("NUL.txt")]
    [InlineData("CONIN$")]
    [InlineData("conout$.log")]
    [InlineData("COM¹")]
    [InlineData("com².txt")]
    [InlineData("COM³.log")]
    [InlineData("COM1 .log")]
    [InlineData("LPT¹")]
    [InlineData("lpt².txt")]
    [InlineData("LPT³.log")]
    [InlineData("LPT¹ .log")]
    [InlineData("trailing.")]
    [InlineData("trailing ")]
    [InlineData(".agent/private")]
    [InlineData(".AGENT/private")]
    [InlineData("cafe\u0301/report.txt")]
    public void Aliases_private_paths_and_non_files_are_rejected(string value)
    {
        Assert.False(WorkspaceRelativeFileTarget.TryParse(value, out var target, out var reason));
        Assert.Null(target);
        Assert.NotNull(reason);
    }

    [Fact]
    public void Target_depth_segment_and_total_bounds_accept_maximum_and_reject_max_plus_one()
    {
        var maximumDepth = string.Join('/', Enumerable.Repeat("a", WorkspaceActionContractLimits.MaxTargetSegments));
        var maximumSegment = new string('a', WorkspaceActionContractLimits.MaxTargetSegmentCharacters);
        var maximumTarget = string.Join('/',
            new string('a', WorkspaceActionContractLimits.MaxTargetSegmentCharacters),
            new string('b', WorkspaceActionContractLimits.MaxTargetSegmentCharacters),
            new string('c', WorkspaceActionContractLimits.MaxTargetSegmentCharacters),
            new string('d', WorkspaceActionContractLimits.MaxTargetCharacters - (3 * WorkspaceActionContractLimits.MaxTargetSegmentCharacters) - 3));
        Assert.Equal(WorkspaceActionContractLimits.MaxTargetCharacters, maximumTarget.Length);
        Assert.True(WorkspaceRelativeFileTarget.TryParse(maximumDepth, out _, out _));
        Assert.True(WorkspaceRelativeFileTarget.TryParse(maximumSegment, out _, out _));
        Assert.True(WorkspaceRelativeFileTarget.TryParse(maximumTarget, out _, out _));

        var tooDeep = string.Join('/', Enumerable.Repeat("a", WorkspaceActionContractLimits.MaxTargetSegments + 1));
        var tooLong = new string('a', WorkspaceActionContractLimits.MaxTargetSegmentCharacters + 1);
        var tooLongTarget = maximumTarget + "e";
        Assert.False(WorkspaceRelativeFileTarget.TryParse(tooDeep, out _, out _));
        Assert.False(WorkspaceRelativeFileTarget.TryParse(tooLong, out _, out _));
        Assert.False(WorkspaceRelativeFileTarget.TryParse(tooLongTarget, out _, out _));
    }
}
